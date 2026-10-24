using System.Runtime.InteropServices;
using GhostScreen.Core;

namespace GhostScreen.Windows;

/// <summary>Virtual screen and primary monitor geometry in physical pixels.</summary>
public sealed record DisplayInfo(Rect VirtualScreen, Rect PrimaryMonitor)
{
    public static DisplayInfo Get()
    {
        var vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        var virtualScreen = new Rect(vx, vy, vw, vh);

        var primaryPoint = new NativeMethods.POINT { X = vx, Y = vy };
        var monitor = NativeMethods.MonitorFromPoint(primaryPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };

        var primary = virtualScreen;
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            primary = new Rect(
                monitorInfo.rcMonitor.Left,
                monitorInfo.rcMonitor.Top,
                monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left,
                monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top);
        }

        return new DisplayInfo(virtualScreen, primary);
    }

    public bool IsFullyOffscreen(Rect rect) => !rect.IntersectsWith(VirtualScreen);

    /// <summary>
    /// A point just outside the virtual desktop. Keeping the window near the original
    /// monitor reduces DPI-change reflow for per-monitor aware apps.
    /// </summary>
    public (int X, int Y) GetParkPosition(Rect windowBounds, int gap = 64)
    {
        var x = VirtualScreen.Right + gap;
        var y = Math.Clamp(windowBounds.Y, PrimaryMonitor.Y + 32, PrimaryMonitor.Bottom - 64);
        return (x, y);
    }
}
