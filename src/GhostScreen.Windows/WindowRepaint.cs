namespace GhostScreen.Windows;

public static class WindowRepaint
{
    /// <summary>
    /// Force a synchronous paint. Useful for windows that were created off-screen
    /// and therefore never received a WM_PAINT from the visibility heuristics.
    /// </summary>
    public static bool Force(IntPtr hwnd)
    {
        return NativeMethods.RedrawWindow(
            hwnd,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.RDW_INVALIDATE |
            NativeMethods.RDW_UPDATENOW |
            NativeMethods.RDW_ALLCHILDREN |
            NativeMethods.RDW_FRAME);
    }
}

public static class DpiAwareness
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public static bool EnablePerMonitorV2() =>
        NativeMethods.SetProcessDpiAwarenessContext(PerMonitorAwareV2);
}
