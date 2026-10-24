namespace GhostScreen.Windows;

public enum FocusRestoreStatus
{
    /// <summary>The target never took the foreground; nothing to restore.</summary>
    NotNeeded,

    /// <summary>The target took the foreground and the original window was restored.</summary>
    Restored,

    /// <summary>The target took the foreground and the original window could not be restored.</summary>
    Stolen,
}

public sealed record FocusRestoreResult(FocusRestoreStatus Status, IntPtr Original, IntPtr Current)
{
    public bool WasStolen => Status == FocusRestoreStatus.Stolen;
}

/// <summary>
/// Protects the human's foreground window around actions that may activate the
/// target. Only wrap UIA actions (some providers call SetForegroundWindow);
/// window messages do not need this guard.
/// </summary>
/// <remarks>
/// This is the last line of defense for UIA actions, which may activate the
/// target even when it has WS_EX_NOACTIVATE. Callers should try every
/// non-activating strategy first; when UIA is unavoidable, execute and restore
/// rather than refusing. Restoration never uses SwitchToThisWindow (a forced
/// Alt+Tab-style switch). If the human moved to a third window, leave it alone.
/// </remarks>
public sealed class FocusGuard : IDisposable
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromMilliseconds(500);

    private readonly IntPtr _target;
    private readonly IntPtr _original;
    private bool _finished;
    private FocusRestoreResult? _result;

    private FocusGuard(IntPtr target)
    {
        _target = target;
        _original = NativeMethods.GetForegroundWindow();
    }

    public static FocusGuard Begin(IntPtr target) => new(target);

    /// <summary>Wait for a delayed UIA activation, then restore the original window if needed.</summary>
    public FocusRestoreResult Finish()
    {
        if (_finished)
        {
            return _result!;
        }

        _finished = true;

        if (_original == IntPtr.Zero || _original == _target)
        {
            _result = new FocusRestoreResult(
                FocusRestoreStatus.NotNeeded,
                _original,
                NativeMethods.GetForegroundWindow());
            return _result;
        }

        var activated = false;
        var deadline = Environment.TickCount64 + (long)SettleTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (NativeMethods.GetForegroundWindow() == _target)
            {
                activated = true;
                break;
            }

            Thread.Sleep(20);
        }

        if (!activated)
        {
            _result = new FocusRestoreResult(
                FocusRestoreStatus.NotNeeded,
                _original,
                NativeMethods.GetForegroundWindow());
            return _result;
        }

        // If the human moved to a third window after the target activated, leave
        // their choice alone. If the target still owns the foreground, restore.
        if (NativeMethods.GetForegroundWindow() != _target)
        {
            _result = new FocusRestoreResult(
                FocusRestoreStatus.NotNeeded,
                _original,
                NativeMethods.GetForegroundWindow());
            return _result;
        }

        if (!NativeMethods.IsWindow(_original) || !TryRestoreForeground(_original))
        {
            _result = new FocusRestoreResult(
                FocusRestoreStatus.Stolen,
                _original,
                NativeMethods.GetForegroundWindow());
            return _result;
        }

        _result = new FocusRestoreResult(
            Stabilize(),
            _original,
            NativeMethods.GetForegroundWindow());
        return _result;
    }

    private FocusRestoreStatus Stabilize()
    {
        // Activation/restore can briefly leave GetForegroundWindow() at 0 or
        // bounce back to the target. Verify it stays on the original for a short
        // window; if a human picked a third window, leave that choice alone.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Thread.Sleep(20);
            var current = NativeMethods.GetForegroundWindow();
            if (current == _original)
            {
                continue;
            }

            if (current == IntPtr.Zero || current == _target)
            {
                if (!TryRestoreForeground(_original))
                {
                    return FocusRestoreStatus.Stolen;
                }

                continue;
            }

            return FocusRestoreStatus.NotNeeded;
        }

        return NativeMethods.GetForegroundWindow() == _original
            ? FocusRestoreStatus.Restored
            : FocusRestoreStatus.Stolen;
    }

    public void Dispose() => Finish();

    /// <summary>Best-effort restore used by diagnostics; product paths should use Begin/Finish.</summary>
    public static bool TryRestoreForeground(IntPtr original)
    {
        if (NativeMethods.GetForegroundWindow() == original)
        {
            return true;
        }

        if (NativeMethods.SetForegroundWindow(original))
        {
            return true;
        }

        // Foreground-lock fallback: attach to the current foreground thread and
        // retry once. Do not use SwitchToThisWindow here; it is a forced switch
        // and can interrupt typing even when SetForegroundWindow is rejected.
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return false;
        }

        if (!NativeMethods.AttachThreadInput(currentThread, foregroundThread, true))
        {
            return false;
        }

        try
        {
            return NativeMethods.SetForegroundWindow(original) &&
                   NativeMethods.GetForegroundWindow() == original;
        }
        finally
        {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }
}
