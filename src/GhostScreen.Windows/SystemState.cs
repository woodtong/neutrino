using System.Runtime.InteropServices;

namespace GhostScreen.Windows;

/// <summary>State that must never change while an agent works in the background.</summary>
public sealed record SystemState(
    IntPtr ForegroundWindow,
    int CursorX,
    int CursorY,
    uint ClipboardSequence,
    uint LastInputTick)
{
    public static SystemState Capture()
    {
        NativeMethods.GetCursorPos(out var cursor);
        var lastInput = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
        };
        NativeMethods.GetLastInputInfo(ref lastInput);

        return new SystemState(
            NativeMethods.GetForegroundWindow(),
            cursor.X,
            cursor.Y,
            NativeMethods.GetClipboardSequenceNumber(),
            lastInput.dwTime);
    }

    public override string ToString() =>
        $"foreground=0x{ForegroundWindow.ToInt64():X} cursor=({CursorX},{CursorY}) " +
        $"clipboardSeq={ClipboardSequence} lastInput={LastInputTick}";
}
