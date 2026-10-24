using GhostScreen.Core;

namespace GhostScreen.Windows;

public enum WindowDpiAwareness
{
    Unknown = -1,
    Unaware = 0,
    SystemAware = 1,
    PerMonitorAware = 2,
}

/// <summary>Immutable snapshot of a top-level window.</summary>
public sealed record WindowInfo(
    IntPtr Handle,
    uint ProcessId,
    string ProcessName,
    string Title,
    string ClassName,
    Rect Bounds,
    Rect ClientBounds,
    bool Visible,
    bool Minimized,
    bool Cloaked,
    bool ToolWindow,
    int DpiScalePercent,
    bool ProtectedFromCapture,
    WindowDpiAwareness DpiAwareness = WindowDpiAwareness.Unknown)
{
    public string HexHandle => $"0x{Handle.ToInt64():X}";

    public override string ToString() =>
        $"{HexHandle} pid={ProcessId} {ProcessName} \"{Title}\" class={ClassName} " +
        $"bounds={Bounds} visible={Visible} min={Minimized} cloak={Cloaked} " +
        $"tool={ToolWindow} dpi={DpiScalePercent}% dpiAwareness={DpiAwareness} " +
        $"protected={ProtectedFromCapture}";
}
