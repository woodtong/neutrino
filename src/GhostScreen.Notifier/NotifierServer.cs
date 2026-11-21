using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Windows.Forms;
using GhostScreen.Core;

namespace GhostScreen.NotifierApp;

/// <summary>
/// Hosts the notifier: one pipe listener, a queue of prompts (only one window at a
/// time), and toasts through the tray icon. It runs inside the tray app when that is
/// up, and as its own process (<c>GhostScreen.Notifier.exe</c>) when the daemon needs
/// to say something and the tray is not running. Either way it survives the failure
/// of the process that sent the request 鈥?that is the whole point of the split.
/// </summary>
public sealed class NotifierServer : IDisposable
{
    private readonly string _pipeName;
    private readonly NotifyIcon? _icon;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<QueuedPrompt> _queue = new();
    private readonly ConcurrentDictionary<string, byte> _handling = new(StringComparer.Ordinal);
    private readonly object _queueGate = new();
    private PromptForm? _current;
    private bool _disposed;
    private bool _pumping;

    /// <summary>Raised when a prompt is queued or finished, for the idle-exit timer.</summary>
    public event EventHandler? Activity;

    public NotifierServer(NotifyIcon? icon = null, string pipeName = NotifierProtocol.PipeName)
    {
        _icon = icon;
        _pipeName = pipeName;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    }

    public int PendingCount
    {
        get
        {
            lock (_queueGate)
            {
                return _queue.Count + (_current is null ? 0 : 1);
            }
        }
    }

    /// <summary>True when this prompt key is already queued or on screen.</summary>
    public bool IsHandling(string key) => _handling.ContainsKey(key);

    /// <summary>
    /// Queue a prompt from inside this process (the tray watches the status endpoint
    /// and pushes tickets the daemon did not announce). Same queue as the pipe path,
    /// so a ticket can never produce two windows.
    /// </summary>
    public Task<NotifierReply> EnqueueAsync(NotifierPrompt prompt)
    {
        var queued = new QueuedPrompt(prompt);
        _handling[prompt.Key] = 0;
        lock (_queueGate)
        {
            _queue.Enqueue(queued);
        }

        Activity?.Invoke(this, EventArgs.Empty);
        Pump();
        return queued.Completion.Task;
    }

    /// <summary>Show a toast through the tray icon, ignoring the reply.</summary>
    public void Notify(NotifierPrompt prompt)
    {
        if (prompt.Kind == "toast")
        {
            ShowToast(prompt);
        }
        else
        {
            _ = EnqueueAsync(prompt);
        }
    }

    public void Start() => _ = AcceptLoopAsync();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();

        lock (_queueGate)
        {
            while (_queue.TryDequeue(out var queued))
            {
                queued.Completion.TrySetResult(new NotifierReply(false, null, "notifier stopped"));
            }
        }

        _current?.Close();
        _current = null;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = HandleAsync(pipe);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            NotifierReply reply;
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, true, 1024, leaveOpen: true);
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                var prompt = string.IsNullOrWhiteSpace(line) ? null : NotifierProtocol.ParsePrompt(line);
                reply = prompt is null
                    ? new NotifierReply(false, null, "bad request")
                    : await DispatchAsync(prompt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                reply = new NotifierReply(false, null, ex.Message);
            }

            try
            {
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n",
                };
                await writer.WriteLineAsync(NotifierProtocol.Format(reply)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The caller gave up (timeout); nothing to do.
            }
        }
    }

    private Task<NotifierReply> DispatchAsync(NotifierPrompt prompt) =>
        prompt.Kind == "toast" ? Task.FromResult(ShowToast(prompt)) : ShowPromptAsync(prompt);

    private NotifierReply ShowToast(NotifierPrompt prompt)
    {
        if (_icon is null)
        {
            return new NotifierReply(false, null, "no tray icon");
        }

        _ui.Post(
            _ =>
            {
                try
                {
                    _icon.BalloonTipTitle = Truncate(prompt.Title, 63);
                    _icon.BalloonTipText = Truncate(prompt.Body, 255);
                    _icon.BalloonTipIcon = prompt.Severity switch
                    {
                        NotifierSeverity.Error => ToolTipIcon.Error,
                        NotifierSeverity.Warning => ToolTipIcon.Warning,
                        _ => ToolTipIcon.Info,
                    };
                    _icon.ShowBalloonTip(Math.Max(1, prompt.TimeoutMs / 1000));
                }
                catch (Exception)
                {
                    // A hidden/absent notification area must not throw into the pipe loop.
                }

                Activity?.Invoke(this, EventArgs.Empty);
            },
            null);

        return new NotifierReply(true, null, null);
    }

    private Task<NotifierReply> ShowPromptAsync(NotifierPrompt prompt)
    {
        var queued = new QueuedPrompt(prompt);
        _handling[prompt.Key] = 0;
        lock (_queueGate)
        {
            _queue.Enqueue(queued);
        }

        Activity?.Invoke(this, EventArgs.Empty);
        Pump();
        return queued.Completion.Task;
    }

    /// <summary>Shows the next queued prompt; re-entrant calls are ignored.</summary>
    private void Pump()
    {
        lock (_queueGate)
        {
            if (_pumping)
            {
                return;
            }

            _pumping = true;
        }

        _ui.Post(_ => ShowNext(), null);
    }

    private async void ShowNext()
    {
        try
        {
            while (true)
            {
                QueuedPrompt next;
                lock (_queueGate)
                {
                    if (_disposed || !_queue.TryDequeue(out next!))
                    {
                        _current = null;
                        return;
                    }
                }

                var form = new PromptForm(next.Prompt);
                _current = form;
                form.Show();
                var action = await form.Completion.ConfigureAwait(true);
                _current = null;
                form.Dispose();
                _handling.TryRemove(next.Prompt.Key, out _);
                next.Completion.TrySetResult(new NotifierReply(true, action, null));
                Activity?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            lock (_queueGate)
            {
                _pumping = false;
            }
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private sealed class QueuedPrompt
    {
        public QueuedPrompt(NotifierPrompt prompt) => Prompt = prompt;

        public NotifierPrompt Prompt { get; }

        public TaskCompletionSource<NotifierReply> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
