using System.Runtime.InteropServices;

namespace GhostScreen.Windows;

/// <summary>Whether a human is currently using the desktop.</summary>
public interface IHumanActivityProbe
{
    bool IsActive(TimeSpan threshold);
}

/// <summary>
/// Uses the system-wide last-input tick. If the probe fails, report active:
/// the strict policy should prefer asking over stealing focus.
/// </summary>
public sealed class LastInputHumanActivityProbe : IHumanActivityProbe
{
    public bool IsActive(TimeSpan threshold)
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
        };

        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return true;
        }

        var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return elapsed < threshold.TotalMilliseconds;
    }
}

/// <summary>Test hook: a fixed activity state.</summary>
public sealed class FixedHumanActivityProbe : IHumanActivityProbe
{
    private readonly bool _active;

    public FixedHumanActivityProbe(bool active) => _active = active;

    public bool IsActive(TimeSpan threshold) => _active;
}
