using System.Runtime.InteropServices;
using System.Text;

namespace GhostScreen.Windows;

/// <summary>What the human appears to be doing with the desktop right now.</summary>
public enum HumanActivityKind
{
    /// <summary>No recent input.</summary>
    None,

    /// <summary>Recent input, but nothing more specific could be inferred.</summary>
    Active,

    /// <summary>Recent pointer work: clicking, dragging, resizing, walking a menu.</summary>
    Pointing,

    /// <summary>Recent input while a text caret is present in the focused window: typing.</summary>
    Typing,
}

/// <summary>
/// One probe result. <see cref="Kind"/> is an <em>inference</em>, never a reading
/// of input content: the gate can only observe that input happened, plus two UI
/// signals (a text caret and the menu / move-size state). GhostScreen never
/// inspects keystrokes or text, by design.
/// </summary>
public sealed record HumanActivitySnapshot(
    bool Active,
    HumanActivityKind Kind,
    long IdleMs,
    string? ForegroundTitle,
    bool HasCaret,
    bool InMenuOrMoveSize)
{
    public static HumanActivitySnapshot Idle { get; } =
        new(false, HumanActivityKind.None, long.MaxValue, null, false, false);

    /// <summary>Stable token used on the wire, in error payloads and in the prompt.</summary>
    public string ActivityToken => Kind switch
    {
        HumanActivityKind.Typing => "typing",
        HumanActivityKind.Pointing => "pointing",
        HumanActivityKind.Active => "active",
        _ => "none",
    };
}

/// <summary>Whether a human is currently using the desktop, and how.</summary>
public interface IHumanActivityProbe
{
    HumanActivitySnapshot Probe(TimeSpan threshold);
}

/// <summary>
/// Uses the system-wide last-input tick, then the foreground GUI thread to tell
/// typing from pointing. If either probe fails, report active: the strict policy
/// should prefer asking over stealing focus.
/// </summary>
public sealed class LastInputHumanActivityProbe : IHumanActivityProbe
{
    private const int MaxTitleLength = 120;

    public HumanActivitySnapshot Probe(TimeSpan threshold)
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
        };

        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return new HumanActivitySnapshot(true, HumanActivityKind.Active, 0, null, false, false);
        }

        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        if (idleMs >= threshold.TotalMilliseconds)
        {
            return new HumanActivitySnapshot(false, HumanActivityKind.None, idleMs, null, false, false);
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var title = ForegroundTitle(foreground);
        var (hasCaret, inMenuOrMoveSize) = FocusedCaretState(foreground);

        // A caret only proves "a text control has focus"; combined with input in
        // the last threshold it is the closest documented signal for "typing".
        // Menu / move-size can only be driven by the pointer, so that one is safe.
        var kind = hasCaret
            ? HumanActivityKind.Typing
            : inMenuOrMoveSize
                ? HumanActivityKind.Pointing
                : HumanActivityKind.Active;

        return new HumanActivitySnapshot(true, kind, idleMs, title, hasCaret, inMenuOrMoveSize);
    }

    private static (bool HasCaret, bool InMenuOrMoveSize) FocusedCaretState(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero)
        {
            return (false, false);
        }

        var thread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        if (thread == 0)
        {
            return (false, false);
        }

        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
        };

        if (!NativeMethods.GetGUIThreadInfo(thread, ref info))
        {
            return (false, false);
        }

        var inMenuOrMoveSize =
            (info.flags & (NativeMethods.GUI_INMENUMODE |
                           NativeMethods.GUI_POPUPMENUMODE |
                           NativeMethods.GUI_INMOVESIZE)) != 0;
        return (info.hwndCaret != IntPtr.Zero, inMenuOrMoveSize);
    }

    private static string? ForegroundTitle(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }

        var length = NativeMethods.GetWindowTextLength(window);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new StringBuilder(Math.Min(length + 1, MaxTitleLength));
        var copied = NativeMethods.GetWindowText(window, buffer, buffer.Capacity);
        return copied <= 0 ? null : buffer.ToString();
    }
}

/// <summary>Test hook: a fixed activity state.</summary>
public sealed class FixedHumanActivityProbe : IHumanActivityProbe
{
    private readonly HumanActivitySnapshot _snapshot;

    public FixedHumanActivityProbe(
        bool active,
        HumanActivityKind kind = HumanActivityKind.Active,
        string? foregroundTitle = null)
        : this(new HumanActivitySnapshot(
            active,
            kind,
            active ? 200 : 10_000,
            foregroundTitle,
            kind == HumanActivityKind.Typing,
            kind == HumanActivityKind.Pointing))
    {
    }

    public FixedHumanActivityProbe(HumanActivitySnapshot snapshot) => _snapshot = snapshot;

    public HumanActivitySnapshot Probe(TimeSpan threshold) => _snapshot;
}
