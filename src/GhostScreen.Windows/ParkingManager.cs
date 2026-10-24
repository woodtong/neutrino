using System.Runtime.InteropServices;
using GhostScreen.Core;

namespace GhostScreen.Windows;

/// <summary>
/// Moves a window completely outside the virtual desktop while keeping it in a
/// renderable (shown, restored) state. Restores the original placement on dispose.
/// </summary>
public static class ParkingManager
{
    public static ParkedWindow Park(
        IntPtr hwnd,
        bool hideFromTaskbar = true,
        bool preventActivation = true)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            throw new ArgumentException("Invalid window handle.", nameof(hwnd));
        }

        var info = WindowEnumerator.Describe(hwnd)
            ?? throw new InvalidOperationException("Window has no usable bounds.");

        var placement = new NativeMethods.WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
        };
        NativeMethods.GetWindowPlacement(hwnd, ref placement);

        var originalExStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        var wasMinimized = info.Minimized;
        var originalBounds = wasMinimized
            ? new Rect(
                placement.rcNormalPosition.Left,
                placement.rcNormalPosition.Top,
                placement.rcNormalPosition.Right - placement.rcNormalPosition.Left,
                placement.rcNormalPosition.Bottom - placement.rcNormalPosition.Top)
            : info.Bounds;
        var wasVisible = info.Visible;

        var display = DisplayInfo.Get();
        var (parkX, parkY) = display.GetParkPosition(originalBounds);

        var moved = NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            parkX,
            parkY,
            originalBounds.Width,
            originalBounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOOWNERZORDER);
        if (!moved)
        {
            throw new InvalidOperationException("SetWindowPos failed while parking.");
        }

        var styleChanged = false;
        var desiredExStyle = originalExStyle.ToInt64();
        if (hideFromTaskbar)
        {
            desiredExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        }

        if (preventActivation)
        {
            desiredExStyle |= NativeMethods.WS_EX_NOACTIVATE;
        }

        if (desiredExStyle != originalExStyle.ToInt64())
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(desiredExStyle));
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
            styleChanged = true;
        }

        if (wasMinimized)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            Thread.Sleep(100);
        }
        else if (!wasVisible)
        {
            // Show at the park position in the same call; some apps reset their
            // position on a separate ShowWindow.
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                parkX,
                parkY,
                originalBounds.Width,
                originalBounds.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW);
        }

        NativeMethods.RECT parkedRect = default;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.GetWindowRect(hwnd, out parkedRect) &&
                display.IsFullyOffscreen(ToRect(parkedRect)))
            {
                break;
            }

            Thread.Sleep(100);
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                parkX,
                parkY,
                originalBounds.Width,
                originalBounds.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOOWNERZORDER);
        }

        if (!NativeMethods.GetWindowRect(hwnd, out parkedRect) ||
            !display.IsFullyOffscreen(ToRect(parkedRect)))
        {
            RestoreOriginal(hwnd, placement, originalExStyle, styleChanged, wasVisible);
            throw new InvalidOperationException(
                $"Window did not stay off-screen (rect={ToRect(parkedRect)}); the app may clamp its position.");
        }

        return new ParkedWindow(
            hwnd,
            originalBounds,
            ToRect(parkedRect),
            placement,
            originalExStyle,
            wasVisible,
            styleChanged);
    }

    /// <summary>Move a window without changing its z-order or activating it.</summary>
    public static bool MoveTo(IntPtr hwnd, Rect bounds)
    {
        return NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOOWNERZORDER);
    }

    private static void RestoreOriginal(
        IntPtr hwnd,
        NativeMethods.WINDOWPLACEMENT placement,
        IntPtr originalExStyle,
        bool styleChanged,
        bool wasVisible)
    {
        if (styleChanged)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, originalExStyle);
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }

        var restore = placement;
        NativeMethods.SetWindowPlacement(hwnd, ref restore);

        if (!wasVisible)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        }
    }

    private static Rect ToRect(NativeMethods.RECT rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
}

public sealed record WindowPlacementData(
    int Length,
    int Flags,
    int ShowCommand,
    int MinX,
    int MinY,
    int MaxX,
    int MaxY,
    int NormalLeft,
    int NormalTop,
    int NormalRight,
    int NormalBottom);

/// <summary>Everything needed to restore a parked window after a crash.</summary>
public sealed record ParkedWindowSnapshot(
    long Hwnd,
    int ProcessId,
    string ProcessName,
    string ClassName,
    string Title,
    WindowPlacementData Placement,
    long OriginalExStyle,
    bool WasVisible,
    bool StyleChanged,
    long ParkedAtUnixMs);

public sealed class ParkedWindow : IDisposable
{
    private readonly NativeMethods.WINDOWPLACEMENT _placement;
    private readonly IntPtr _originalExStyle;
    private readonly bool _wasVisible;
    private readonly bool _styleChanged;
    private bool _restored;

    internal ParkedWindow(
        IntPtr handle,
        Rect originalBounds,
        Rect parkedBounds,
        NativeMethods.WINDOWPLACEMENT placement,
        IntPtr originalExStyle,
        bool wasVisible,
        bool styleChanged)
    {
        Handle = handle;
        OriginalBounds = originalBounds;
        ParkedBounds = parkedBounds;
        _placement = placement;
        _originalExStyle = originalExStyle;
        _wasVisible = wasVisible;
        _styleChanged = styleChanged;
    }

    public IntPtr Handle { get; }

    public Rect OriginalBounds { get; }

    public Rect ParkedBounds { get; }

    public WindowPlacementData Placement => new(
        _placement.length,
        _placement.flags,
        _placement.showCmd,
        _placement.ptMinPosition.X,
        _placement.ptMinPosition.Y,
        _placement.ptMaxPosition.X,
        _placement.ptMaxPosition.Y,
        _placement.rcNormalPosition.Left,
        _placement.rcNormalPosition.Top,
        _placement.rcNormalPosition.Right,
        _placement.rcNormalPosition.Bottom);

    public long OriginalExStyle => _originalExStyle.ToInt64();

    public bool WasVisible => _wasVisible;

    public bool StyleChanged => _styleChanged;

    public ParkedWindowSnapshot ToSnapshot(WindowInfo info) => new(
        Handle.ToInt64(),
        (int)info.ProcessId,
        info.ProcessName,
        info.ClassName,
        info.Title,
        Placement,
        OriginalExStyle,
        WasVisible,
        StyleChanged,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public void Restore()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;

        if (_styleChanged)
        {
            NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, _originalExStyle);
            NativeMethods.SetWindowPos(
                Handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }

        var placement = _placement;
        NativeMethods.SetWindowPlacement(Handle, ref placement);

        if (!_wasVisible)
        {
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
        }
    }

    public void Dispose() => Restore();
}
