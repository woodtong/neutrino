using GhostScreen.Capture;
using GhostScreen.Core;
using GhostScreen.Input;
using GhostScreen.Web;
using GhostScreen.Windows;

namespace GhostScreen.Daemon;

public sealed class TargetException : Exception
{
    public TargetException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record UiaObservation(int Epoch, IReadOnlyList<UiaElement> Elements);

/// <summary>
/// Result of a native input action. <see cref="Strategy"/> is "message" when the
/// action went to an HWND child without activating the window, or "uia" when the
/// focus-stealing fallback was explicitly allowed.
/// </summary>
public sealed record NativeActionResult(bool Ok, string Strategy, FocusRestoreStatus Focus);

/// <summary>A target is either a parked native window or an owned headless browser page.</summary>
public interface ITarget : IDisposable
{
    string Id { get; }

    string Kind { get; }

    DateTimeOffset CreatedAt { get; }

    DateTimeOffset LastUsed { get; }

    object ToDto();

    void Close();
}

/// <summary>A parked window owned by one agent session. Operations are serialized.</summary>
public sealed class TargetSession : ITarget
{
    private readonly object _gate = new();
    private readonly ParkedWindow _parked;
    private IReadOnlyList<UiaElement> _lastObservation = [];
    private int _epoch;
    private bool _closed;
    private int _consecutiveTimeouts;
    private string? _degradedReason;
    private Func<IntPtr, CaptureOptions, CaptureResult> _capture = CaptureEngine.Capture;

    internal TargetSession(string id, WindowInfo info, ParkedWindow parked)
    {
        Id = id;
        Info = info;
        _parked = parked;
        CreatedAt = DateTimeOffset.UtcNow;
        LastUsed = CreatedAt;
    }

    public string Id { get; }

    public string Kind => "app";

    internal ParkedWindow Parked => _parked;

    public WindowInfo Info { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastUsed { get; private set; }

    public CaptureResult Capture(CaptureOptions options)
    {
        lock (_gate)
        {
            EnsureOpen();
            try
            {
                var result = _capture(Info.Handle, options);
                _consecutiveTimeouts = 0;
                Touch();
                return result;
            }
            catch (CaptureException ex) when (ex.Code is "CAPTURE_TIMEOUT" or "TARGET_HUNG")
            {
                if (ex.Code == "TARGET_HUNG")
                {
                    _degradedReason = "TARGET_HUNG";
                }
                else
                {
                    _consecutiveTimeouts++;
                    if (_consecutiveTimeouts >= 2)
                    {
                        _degradedReason = "TARGET_HUNG";
                    }
                }

                Touch();
                throw;
            }
        }
    }

    public UiaObservation Observe(int maxNodes = 100)
    {
        lock (_gate)
        {
            EnsureOpen();
            var elements = UiaDriver.Snapshot(Info.Handle, maxNodes);
            _epoch++;
            _lastObservation = elements;
            Touch();
            return new UiaObservation(_epoch, elements);
        }
    }

    public NativeActionResult ClickElement(string name, bool strictNoFocusSteal = false)
    {
        lock (_gate)
        {
            EnsureOpen();
            var element = UiaDriver.FindByName(Info.Handle, name)
                ?? throw new TargetException("ELEMENT_NOT_FOUND", $"UIA element not found: {name}");
            var result = ClickElementCore(element, strictNoFocusSteal);
            Touch();
            return result;
        }
    }

    public NativeActionResult ClickRef(string reference, bool strictNoFocusSteal = false)
    {
        lock (_gate)
        {
            EnsureOpen();
            var element = _lastObservation.FirstOrDefault(candidate =>
                candidate.Ref.Equals(reference, StringComparison.OrdinalIgnoreCase));

            if (element is null)
            {
                throw new TargetException(
                    "ELEMENT_STALE",
                    $"Ref '{reference}' is not in the latest observation (epoch {_epoch}); call observe again.");
            }

            var result = ClickElementCore(element, strictNoFocusSteal);
            Touch();
            return result;
        }
    }

    private NativeActionResult ClickElementCore(UiaElement element, bool strictNoFocusSteal)
    {
        // Re-resolve the element so a layout change between observe and click
        // cannot send a message to a stale rectangle.
        var fresh = ResolveFresh(element);

        if (fresh.Bounds.Width > 0 &&
            fresh.Bounds.Height > 0 &&
            MessageInput.TryClickChildAtScreenPoint(
                Info.Handle,
                fresh.Bounds.X + fresh.Bounds.Width / 2,
                fresh.Bounds.Y + fresh.Bounds.Height / 2,
                out _))
        {
            return new NativeActionResult(true, "message", FocusRestoreStatus.NotNeeded);
        }

        EnsureUiaAllowed(strictNoFocusSteal, "click_element");

        if (!HasActionPattern(fresh))
        {
            throw new TargetException(
                "ELEMENT_NOT_ACTIONABLE",
                $"Element '{fresh.Name}' ({fresh.AutomationId}) exposes no Invoke/Toggle/SelectionItem/ExpandCollapse pattern.");
        }

        using var guard = FocusGuard.Begin(Info.Handle);
        var ok = UiaDriver.Activate(Info.Handle, fresh);
        if (!ok)
        {
            throw new TargetException(
                "ELEMENT_NOT_ACTIONABLE",
                $"Element '{fresh.Name}' ({fresh.AutomationId}) could not be activated.");
        }

        return new NativeActionResult(true, "uia", guard.Finish().Status);
    }

    private static bool HasActionPattern(UiaElement element) =>
        element.Patterns.Any(pattern => pattern is "Invoke" or "Toggle" or "SelectionItem" or "ExpandCollapse");

    private UiaElement ResolveFresh(UiaElement element)
    {
        var fresh = !string.IsNullOrWhiteSpace(element.AutomationId)
            ? UiaDriver.FindByAutomationId(Info.Handle, element.AutomationId)
            : UiaDriver.FindByName(Info.Handle, element.Name);

        return fresh ?? throw new TargetException(
            "ELEMENT_STALE",
            $"Element '{element.Name}' ({element.AutomationId}) is no longer present; call observe again.");
    }

    public NativeActionResult ClickPoint(int clientX, int clientY, bool strictNoFocusSteal = false)
    {
        lock (_gate)
        {
            EnsureOpen();
            InputRouter.EnsureMessageInput(Info);
            ValidatePoint(clientX, clientY);

            var screenX = Info.ClientBounds.X + clientX;
            var screenY = Info.ClientBounds.Y + clientY;

            if (MessageInput.TryClickChildAtScreenPoint(Info.Handle, screenX, screenY, out _))
            {
                Touch();
                return new NativeActionResult(true, "message", FocusRestoreStatus.NotNeeded);
            }

            // Single-HWND surfaces (WPF/UWP) ignore synthetic mouse messages and
            // have no child HWND to hit; resolve the element at the point via UIA.
            if (IsSingleSurface(Info.ClassName))
            {
                EnsureUiaAllowed(strictNoFocusSteal, "click");
                using var guard = FocusGuard.Begin(Info.Handle);
                if (!UiaDriver.ActivateAtPoint(Info.Handle, screenX, screenY))
                {
                    throw new TargetException(
                        "ELEMENT_NOT_ACTIONABLE",
                        $"No actionable UIA element at client ({clientX},{clientY}) in {Info.ClassName}.");
                }

                Touch();
                return new NativeActionResult(true, "uia-point", guard.Finish().Status);
            }

            var ok = MessageInput.ClickClientPoint(Info.Handle, clientX, clientY);
            Touch();
            return new NativeActionResult(ok, "message", FocusRestoreStatus.NotNeeded);
        }
    }

    private static bool IsSingleSurface(string className) =>
        className.Contains("HwndWrapper", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("Windows.UI.Core", StringComparison.OrdinalIgnoreCase);

    public bool Scroll(int clientX, int clientY, int delta)
    {
        lock (_gate)
        {
            EnsureOpen();
            InputRouter.EnsureMessageInput(Info);
            ValidatePoint(clientX, clientY);

            var result = MessageInput.ScrollClientPoint(Info.Handle, clientX, clientY, delta);
            Touch();
            return result;
        }
    }

    public bool Drag(int startX, int startY, int endX, int endY, int durationMs)
    {
        lock (_gate)
        {
            EnsureOpen();
            InputRouter.EnsureMessageInput(Info);
            ValidatePoint(startX, startY);
            ValidatePoint(endX, endY);

            var result = MessageInput.DragClientPoint(Info.Handle, startX, startY, endX, endY, durationMs);
            Touch();
            return result;
        }
    }

    public bool PressKey(IReadOnlyList<string> keys)
    {
        lock (_gate)
        {
            EnsureOpen();
            InputRouter.EnsureMessageInput(Info);
            if (keys.Count == 0)
            {
                throw new TargetException("INVALID_ARGUMENTS", "key requires at least one key.");
            }

            var main = keys[^1];
            var modifiers = keys.Take(keys.Count - 1).ToArray();
            var result = MessageInput.PressKey(Info.Handle, main, modifiers);
            Touch();
            return result;
        }
    }

    public NativeActionResult SetText(string automationId, string text, bool strictNoFocusSteal = false)
    {
        lock (_gate)
        {
            EnsureOpen();
            var element = UiaDriver.FindByAutomationId(Info.Handle, automationId)
                ?? throw new TargetException("ELEMENT_NOT_FOUND", $"UIA element not found: {automationId}");

            if (element.Bounds.Width > 0 &&
                element.Bounds.Height > 0 &&
                MessageInput.TrySetChildTextAtScreenPoint(
                    Info.Handle,
                    element.Bounds.X + element.Bounds.Width / 2,
                    element.Bounds.Y + element.Bounds.Height / 2,
                    text,
                    out _))
            {
                Touch();
                return new NativeActionResult(true, "message", FocusRestoreStatus.NotNeeded);
            }

            EnsureUiaAllowed(strictNoFocusSteal, "set_text");

            if (!element.Patterns.Contains("Value", StringComparer.Ordinal))
            {
                throw new TargetException(
                    "ELEMENT_NOT_ACTIONABLE",
                    $"Element '{element.Name}' ({element.AutomationId}) does not expose ValuePattern.");
            }

            using var guard = FocusGuard.Begin(Info.Handle);
            var ok = UiaDriver.SetValue(Info.Handle, automationId, text);
            Touch();
            return new NativeActionResult(ok, "uia", guard.Finish().Status);
        }
    }

    private static void EnsureUiaAllowed(bool strictNoFocusSteal, string action)
    {
        if (strictNoFocusSteal)
        {
            throw new TargetException(
                "FOCUS_STEAL_NOT_ALLOWED",
                $"{action} needs the UIA fallback, which briefly activates the target. " +
                "strict_no_focus_steal=true refused it; set it to false to execute and restore focus.");
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _parked.Dispose();
        }
    }

    public object ToDto() => new
    {
        id = Id,
        kind = "app",
        state = _closed ? "closed" : _degradedReason is null ? "ready" : "degraded",
        degraded_reason = _degradedReason,
        title = Info.Title,
        process = new { pid = Info.ProcessId, name = Info.ProcessName },
        window = new
        {
            hwnd = Info.HexHandle,
            rect = Info.Bounds.ToString(),
            client = Info.ClientBounds.ToString(),
            dpi = Info.DpiScalePercent,
            dpi_awareness = Info.DpiAwareness.ToString(),
        },
        warnings = Info.DpiAwareness == WindowDpiAwareness.Unaware
            ? new[] { "DPI_UNAWARE" }
            : Array.Empty<string>(),
        capabilities = new
        {
            capture = new[] { "printwindow", "wgc" },
            observe = new[] { "uia" },
            input = InputRouter.GetInputStrategies(Info),
            web = false,
        },
        createdAt = CreatedAt,
        lastUsed = LastUsed,
    };

    public void Dispose() => Close();

    internal void SetCaptureForTests(Func<IntPtr, CaptureOptions, CaptureResult> capture)
    {
        lock (_gate)
        {
            _capture = capture;
        }
    }

    private void EnsureOpen()
    {
        if (_closed)
        {
            throw new TargetException("TARGET_CLOSED", $"Target {Id} is closed.");
        }

        if (_degradedReason is not null)
        {
            throw new TargetException(
                "TARGET_HUNG",
                $"Target {Id} is degraded ({_degradedReason}); close it and attach again.");
        }
    }

    private void ValidatePoint(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Info.ClientBounds.Width || y >= Info.ClientBounds.Height)
        {
            throw new TargetException(
                "INVALID_ARGUMENTS",
                $"Point ({x},{y}) is outside the client area {Info.ClientBounds.Width}x{Info.ClientBounds.Height}.");
        }
    }

    private void Touch() => LastUsed = DateTimeOffset.UtcNow;
}

/// <summary>Owns parked windows and headless browser pages for the lifetime of the MCP/daemon process.</summary>
public sealed class TargetManager : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ITarget> _targets = new(StringComparer.Ordinal);
    private readonly TargetJournal _journal;

    public TargetManager(TargetJournal? journal = null)
    {
        _journal = journal ?? new TargetJournal();
        LastRecovery = _journal.Recover();
    }

    /// <summary>What the crash journal restored/cleaned when this manager started.</summary>
    public RecoveryReport LastRecovery { get; }

    public TargetSession Attach(IntPtr hwnd, bool confirm)
    {
        if (!confirm)
        {
            throw new TargetException(
                "CONFIRM_REQUIRED",
                "attach moves the window off-screen; pass confirm=true for a window you are authorized to move.");
        }

        // Refuse before touching the window: SetWindowPos can block on a hung
        // message thread, and a stuck attach is worse than a clear error.
        if (WindowHealth.IsHung(hwnd))
        {
            throw new TargetException(
                "TARGET_HUNG",
                "Window is not responding; refusing to move it. Close/reopen the app and retry.");
        }

        var info = WindowEnumerator.Describe(hwnd)
            ?? throw new TargetException("NOT_RENDERABLE", "Window handle is invalid or has no bounds.");

        if (info.ProtectedFromCapture)
        {
            throw new TargetException("PROTECTED_CONTENT", "Window is excluded from capture.");
        }

        ParkedWindow? parked = null;
        try
        {
            parked = ParkingManager.Park(hwnd);
            WindowRepaint.Force(hwnd);
            Thread.Sleep(150);
            WindowRepaint.Force(hwnd);
            var refreshed = WindowEnumerator.Describe(hwnd) ?? info;
            var id = "tgt_" + Guid.NewGuid().ToString("N")[..6];
            var session = new TargetSession(id, refreshed, parked);
            _journal.RecordApp(id, session);

            lock (_sync)
            {
                _targets[id] = session;
            }

            return session;
        }
        catch
        {
            parked?.Dispose();
            throw;
        }
    }

    public TargetSession AttachByTitle(string title, bool confirm)
    {
        var matches = WindowEnumerator.List()
            .Where(window => window.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            0 => throw new TargetException("TARGET_NOT_FOUND", $"No visible window title contains '{title}'."),
            > 1 => throw new TargetException(
                "TARGET_AMBIGUOUS",
                $"{matches.Length} windows match '{title}'; pass an explicit hwnd."),
            _ => Attach(matches[0].Handle, confirm),
        };
    }

    /// <summary>
    /// Starts a private headless Edge with a temporary profile and navigates it to <paramref name="options"/>.
    /// No visible window is created and no user profile is touched.
    /// </summary>
    public async Task<WebTargetSession> OpenWebAsync(
        WebLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        var url = EdgeBrowser.NormalizeUrl(options.Url);
        EdgeBrowser? browser = null;
        EdgeCdpClient? client = null;

        try
        {
            browser = await EdgeBrowser.LaunchAsync(options with { Url = url }, cancellationToken)
                .ConfigureAwait(false);
            client = await EdgeCdpClient.ConnectAsync(browser.Port, null, cancellationToken)
                .ConfigureAwait(false);
            await client.SetViewportAsync(
                options.Width,
                options.Height,
                options.DeviceScaleFactor,
                options.Mobile).ConfigureAwait(false);
            await client.NavigateAsync(url).ConfigureAwait(false);

            var page = await client.GetPageInfoAsync().ConfigureAwait(false);
            var id = "web_" + Guid.NewGuid().ToString("N")[..6];
            var session = new WebTargetSession(
                id,
                browser,
                client,
                options,
                page.Url,
                page.Title,
                page.Width,
                page.Height);
            _journal.RecordWeb(id, session);

            lock (_sync)
            {
                _targets[id] = session;
            }

            return session;
        }
        catch (Exception ex)
        {
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best effort.
                }
            }

            if (browser is not null)
            {
                try
                {
                    await browser.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best effort.
                }
            }

            throw ex switch
            {
                WebLaunchException launch => new TargetException(launch.Code, launch.Message),
                TimeoutException => new TargetException("NAVIGATION_TIMEOUT", ex.Message),
                TargetException => ex,
                _ => new TargetException("WEB_LAUNCH_FAILED", ex.Message),
            };
        }
    }

    /// <summary>
    /// Attach to an already-running Chromium/Electron DevTools endpoint without
    /// launching or owning the browser. Closing the target detaches only.
    /// </summary>
    public async Task<WebTargetSession> AttachCdpAsync(
        int port,
        string? pageUrlContains = null,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65_535)
        {
            throw new TargetException("INVALID_ARGUMENTS", "port must be between 1 and 65535.");
        }

        EdgeCdpClient? client = null;

        try
        {
            client = await EdgeCdpClient.ConnectAsync(port, pageUrlContains, cancellationToken)
                .ConfigureAwait(false);
            var page = await client.GetPageInfoAsync().ConfigureAwait(false);
            var id = "cdp_" + Guid.NewGuid().ToString("N")[..6];
            var session = new WebTargetSession(
                id,
                client,
                page.Url,
                page.Title,
                page.Width,
                page.Height,
                page.DevicePixelRatio);

            lock (_sync)
            {
                _targets[id] = session;
            }

            return session;
        }
        catch (TargetException)
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            throw new TargetException(
                "CDP_CONNECT_FAILED",
                $"Cannot attach to DevTools port {port}: {ex.Message}");
        }
    }

    public IReadOnlyList<ITarget> List()
    {
        lock (_sync)
        {
            return _targets.Values.ToArray();
        }
    }

    public ITarget Get(string id)
    {
        lock (_sync)
        {
            if (_targets.TryGetValue(id, out var session))
            {
                return session;
            }
        }

        throw new TargetException("TARGET_NOT_FOUND", $"Target '{id}' does not exist.");
    }

    public TargetSession GetNative(string id) =>
        Get(id) as TargetSession
        ?? throw new TargetException("TARGET_KIND_MISMATCH", $"Target '{id}' is not an application window.");

    public WebTargetSession GetWeb(string id) =>
        Get(id) as WebTargetSession
        ?? throw new TargetException("TARGET_KIND_MISMATCH", $"Target '{id}' is not a web page.");

    public bool Close(string id)
    {
        ITarget? session;
        lock (_sync)
        {
            if (!_targets.Remove(id, out session))
            {
                return false;
            }
        }

        session.Close();
        _journal.Remove(id);
        return true;
    }

    public void CloseAll()
    {
        ITarget[] sessions;
        lock (_sync)
        {
            sessions = _targets.Values.ToArray();
            _targets.Clear();
        }

        foreach (var session in sessions)
        {
            try
            {
                session.Close();
            }
            catch
            {
                // Best effort shutdown.
            }
            finally
            {
                _journal.Remove(session.Id);
            }
        }
    }

    public void Dispose() => CloseAll();
}
