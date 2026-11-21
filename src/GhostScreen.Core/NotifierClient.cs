using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace GhostScreen.Core;

/// <summary>How loud a notification is; the prompt window shows a coloured edge.</summary>
public enum NotifierSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One notification. `toast` is a transient balloon; `prompt` is a small
/// non-activating window with buttons and an optional countdown.
/// </summary>
public sealed record NotifierPrompt(
    string Kind,
    string Key,
    string Title,
    string Body,
    NotifierSeverity Severity = NotifierSeverity.Info,
    int TimeoutMs = 0,
    string? TimeoutAction = null,
    string? CountdownFormat = null,
    string[]? ActionIds = null,
    string[]? ActionLabels = null);

/// <summary>What the human did with a prompt (null action = dismissed without choosing).</summary>
public sealed record NotifierReply(bool Ok, string? Action, string? Error);

public interface INotifier
{
    Task<NotifierReply?> SendAsync(NotifierPrompt prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fire-and-forget toast, de-duplicated by <paramref name="key"/>. Returns false
    /// when the same key was sent inside its cooldown. Never throws.
    /// </summary>
    bool TryToast(
        string key,
        string title,
        string body,
        NotifierSeverity severity = NotifierSeverity.Info,
        TimeSpan? cooldown = null,
        int ttlMs = 8_000);
}

/// <summary>Wire format for the notifier pipe: one JSON line each way.</summary>
public static class NotifierProtocol
{
    public const string PipeName = "GhostScreen.Notifier";
    public const string ExeName = "GhostScreen.Notifier.exe";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Format<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static NotifierPrompt? ParsePrompt(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<NotifierPrompt>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static NotifierReply? ParseReply(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<NotifierReply>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Talks to the notifier process over a named pipe. The notifier is deliberately a
/// separate process: it must still be able to tell the human something when this
/// process (the MCP daemon) is the thing that is broken.
/// </summary>
public sealed class NotifierClient : INotifier, IDisposable
{
    private readonly string _pipeName;
    private readonly string? _exePath;
    private readonly bool _spawnIfMissing;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);
    private bool _disposed;

    public NotifierClient(
        string pipeName = NotifierProtocol.PipeName,
        string? exePath = null,
        bool spawnIfMissing = true)
    {
        _pipeName = pipeName;
        _exePath = exePath;
        _spawnIfMissing = spawnIfMissing;
    }

    /// <summary>
    /// Sends one request. Returns null when no notifier could be reached; a missing
    /// notifier is never an error — it must not affect the tool call it reports on.
    /// </summary>
    public async Task<NotifierReply?> SendAsync(NotifierPrompt prompt, CancellationToken cancellationToken = default)
    {
        var reply = await SendOnceAsync(prompt, cancellationToken).ConfigureAwait(false);
        if (reply is not null || !_spawnIfMissing)
        {
            return reply;
        }

        if (!TrySpawnServer())
        {
            return null;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);
        return await SendOnceAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fire-and-forget toast, de-duplicated by <paramref name="key"/> so a condition
    /// that persists (an agent waiting on the human) does not re-notify every poll.
    /// </summary>
    public bool TryToast(
        string key,
        string title,
        string body,
        NotifierSeverity severity = NotifierSeverity.Info,
        TimeSpan? cooldown = null,
        int ttlMs = 8_000)
    {
        if (_disposed || !ShouldSend(key, cooldown ?? TimeSpan.FromSeconds(60)))
        {
            return false;
        }

        _ = SendAsync(new NotifierPrompt("toast", key, title, body, severity, TimeoutMs: ttlMs));
        return true;
    }

    /// <summary>True when the toast is allowed through the per-key cooldown.</summary>
    public bool ShouldSend(string key, TimeSpan cooldown)
    {
        var now = DateTimeOffset.UtcNow;
        var allowed = false;
        _lastSent.AddOrUpdate(
            key,
            _ => { allowed = true; return now; },
            (_, previous) =>
            {
                if (now - previous < cooldown)
                {
                    return previous;
                }

                allowed = true;
                return now;
            });

        return allowed;
    }

    public void Dispose() => _disposed = true;

    private async Task<NotifierReply?> SendOnceAsync(NotifierPrompt prompt, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(2));
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            }

            await using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            })
            {
                await writer.WriteLineAsync(NotifierProtocol.Format(prompt)).ConfigureAwait(false);
            }

            var deadline = prompt.Kind == "toast"
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromMilliseconds(Math.Max(15_000, prompt.TimeoutMs + 15_000));

            using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            read.CancelAfter(deadline);

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(read.Token).ConfigureAwait(false);
            return line is null ? null : NotifierProtocol.ParseReply(line);
        }
        catch (Exception)
        {
            // A notification channel that is down must never break the caller.
            return null;
        }
    }

    private bool TrySpawnServer()
    {
        var exe = _exePath ?? FindExe();
        if (exe is null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return process is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? FindExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, NotifierProtocol.ExeName),
            Path.Combine(baseDir, "..", NotifierProtocol.ExeName),
            Path.Combine(baseDir, "..", "GhostScreen.Notifier", NotifierProtocol.ExeName),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (Exception)
            {
                // Ignore malformed paths and keep looking.
            }
        }

        return null;
    }
}

/// <summary>
/// The human-facing copy for a consent ticket. It lives here, not in the window, so
/// the wording is identical whether the prompt is shown by the tray or by a notifier
/// process the daemon had to start on its own.
/// </summary>
public static class ConsentPromptCopy
{
    public static NotifierPrompt Build(
        string consentId,
        string agent,
        string targetTitle,
        string action,
        string activity,
        string? activityDetail,
        long remainingMs,
        int deferrals,
        int maxDeferrals)
    {
        var ids = new List<string> { "deny" };
        var labels = new List<string> { NotifierText.ActionDeny };
        if (deferrals < maxDeferrals)
        {
            ids.Add("defer");
            labels.Add(NotifierText.ActionDefer);
        }

        ids.Add("allow");
        labels.Add(NotifierText.ActionAllow);

        var name = Short(activityDetail, 24);
        var body = string.Join(
            "\r\n",
            NotifierText.ConsentLine1(Short(agent, 30), Short(targetTitle, 30), NotifierText.AskAction(action)),
            NotifierText.ConsentLine2,
            NotifierText.ConsentLine3(NotifierText.ActivityText(activity, name)),
            NotifierText.ConsentLine4(NotifierText.ActivityVerb(activity)));

        var countdown = NotifierText.Countdown(
            deferrals > 0 ? FormattableString.Invariant($"{deferrals}/{maxDeferrals}") : null);

        return new NotifierPrompt(
            "prompt",
            "consent:" + consentId,
            NotifierText.ConsentTitle,
            body,
            NotifierSeverity.Warning,
            TimeoutMs: (int)Math.Clamp(remainingMs, 1_000, 600_000),
            TimeoutAction: "allow",
            CountdownFormat: countdown,
            ActionIds: ids.ToArray(),
            ActionLabels: labels.ToArray());
    }

    private static string Short(string? value, int max) =>
        string.IsNullOrWhiteSpace(value)
            ? NotifierText.AnotherWindow
            : value.Length <= max ? value : value[..max] + "…";
}
