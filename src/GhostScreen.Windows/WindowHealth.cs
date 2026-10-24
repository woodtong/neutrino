namespace GhostScreen.Windows;

/// <summary>Cheap, non-moving window health probes.</summary>
public static class WindowHealth
{
    /// <summary>
    /// True when Windows considers the window unresponsive. Must be called
    /// before window queries that clear the flag (for example Describe).
    /// </summary>
    public static bool IsHung(IntPtr hwnd) => NativeMethods.IsHungAppWindow(hwnd);
}
