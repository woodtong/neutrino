namespace GhostScreen.Windows;

/// <summary>
/// Graceful in-place window close. Posts <c>WM_CLOSE</c> and waits for the window
/// to be destroyed, so an agent-only window never reappears on the user's desktop
/// and leaves no orphan behind.
/// </summary>
public static class WindowCloser
{
    /// <summary>
    /// Posts <c>WM_CLOSE</c> to the window and waits up to <paramref name="timeout"/>
    /// for the window to be destroyed. Returns true when the window is gone (or was
    /// already gone); false when the app kept it alive — typically a save-changes
    /// prompt — leaving the caller to decide how to recover.
    /// </summary>
    public static bool RequestClose(IntPtr hwnd, TimeSpan timeout)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return true;
        }

        if (!NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
        {
            return !NativeMethods.IsWindow(hwnd);
        }

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return !NativeMethods.IsWindow(hwnd);
    }
}
