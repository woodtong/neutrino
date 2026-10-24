using System.Runtime.InteropServices;
using System.Text;

namespace GhostScreen.Windows;

/// <summary>
/// Restores a window recorded in the crash journal. Every step is verified
/// against the live window to avoid touching a handle that was recycled.
/// </summary>
public static class WindowRecovery
{
    public static bool TryRestore(ParkedWindowSnapshot snapshot, out string reason)
    {
        var hwnd = new IntPtr(snapshot.Hwnd);

        if (!NativeMethods.IsWindow(hwnd))
        {
            reason = "window no longer exists";
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId != (uint)snapshot.ProcessId)
        {
            reason = $"process changed ({snapshot.ProcessId} -> {processId})";
            return false;
        }

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        if (!string.Equals(className.ToString(), snapshot.ClassName, StringComparison.Ordinal))
        {
            reason = "window class changed";
            return false;
        }

        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GWL_EXSTYLE,
            new IntPtr(snapshot.OriginalExStyle));
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

        var placement = ToNative(snapshot.Placement);
        if (!NativeMethods.SetWindowPlacement(hwnd, ref placement))
        {
            reason = "SetWindowPlacement failed";
            return false;
        }

        if (!snapshot.WasVisible)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        }

        reason = "restored";
        return true;
    }

    private static NativeMethods.WINDOWPLACEMENT ToNative(WindowPlacementData data) => new()
    {
        length = data.Length > 0
            ? data.Length
            : Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
        flags = data.Flags,
        showCmd = data.ShowCommand,
        ptMinPosition = new NativeMethods.POINT { X = data.MinX, Y = data.MinY },
        ptMaxPosition = new NativeMethods.POINT { X = data.MaxX, Y = data.MaxY },
        rcNormalPosition = new NativeMethods.RECT
        {
            Left = data.NormalLeft,
            Top = data.NormalTop,
            Right = data.NormalRight,
            Bottom = data.NormalBottom,
        },
    };
}
