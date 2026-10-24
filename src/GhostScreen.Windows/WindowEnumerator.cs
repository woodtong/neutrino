using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GhostScreen.Core;

namespace GhostScreen.Windows;

/// <summary>Enumerates and describes top-level windows for the current interactive session.</summary>
public static class WindowEnumerator
{
    public static IReadOnlyList<WindowInfo> List(bool includeInvisible = false, bool includeOwnProcess = false)
    {
        var list = new List<WindowInfo>();
        var ownPid = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var info = Describe(hwnd);
            if (info is null)
            {
                return true;
            }

            if (!includeOwnProcess && info.ProcessId == ownPid)
            {
                return true;
            }

            if (!includeInvisible && (!info.Visible || info.Cloaked))
            {
                return true;
            }

            list.Add(info);
            return true;
        }, IntPtr.Zero);

        return list
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static WindowInfo? Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        var titleLength = NativeMethods.GetWindowTextLength(hwnd);
        var titleBuilder = new StringBuilder(Math.Max(titleLength + 1, 2));
        NativeMethods.GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);

        var classBuilder = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, classBuilder, classBuilder.Capacity);

        var client = GetClientScreenBounds(hwnd);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var cloaked = false;
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloakValue, sizeof(int)) == 0)
        {
            cloaked = cloakValue != 0;
        }

        var protectedFromCapture = false;
        if (NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity))
        {
            protectedFromCapture = (affinity & NativeMethods.WDA_EXCLUDEFROMCAPTURE) != 0;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var toolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;

        return new WindowInfo(
            hwnd,
            pid,
            GetProcessName(pid),
            titleBuilder.ToString(),
            classBuilder.ToString(),
            new Rect(rect.Left, rect.Top, width, height),
            client,
            NativeMethods.IsWindowVisible(hwnd),
            NativeMethods.IsIconic(hwnd),
            cloaked,
            toolWindow,
            (int)Math.Round(dpi / 96.0 * 100),
            protectedFromCapture,
            GetDpiAwareness(hwnd));
    }

    private static WindowDpiAwareness GetDpiAwareness(IntPtr hwnd)
    {
        try
        {
            var context = NativeMethods.GetWindowDpiAwarenessContext(hwnd);
            return NativeMethods.GetAwarenessFromDpiAwarenessContext(context) switch
            {
                0 => WindowDpiAwareness.Unaware,
                1 => WindowDpiAwareness.SystemAware,
                2 => WindowDpiAwareness.PerMonitorAware,
                _ => WindowDpiAwareness.Unknown,
            };
        }
        catch
        {
            return WindowDpiAwareness.Unknown;
        }
    }

    public static WindowInfo? FindByProcessId(uint pid, bool visibleOnly = false)
    {
        WindowInfo? best = null;
        var bestArea = 0L;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid != pid)
            {
                return true;
            }

            var info = Describe(hwnd);
            if (info is null)
            {
                return true;
            }

            if (visibleOnly && (!info.Visible || info.Minimized || info.Cloaked))
            {
                return true;
            }

            var area = (long)info.Bounds.Width * info.Bounds.Height;
            if (area > bestArea)
            {
                best = info;
                bestArea = area;
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }

    public static WindowInfo? FindByTitle(string contains, bool visibleOnly = false)
    {
        return List(includeInvisible: !visibleOnly)
            .FirstOrDefault(w => w.Title.Contains(contains, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<WindowInfo> Children(IntPtr parent)
    {
        var list = new List<WindowInfo>();
        NativeMethods.EnumChildWindows(parent, (hwnd, _) =>
        {
            var info = Describe(hwnd);
            if (info is not null)
            {
                list.Add(info);
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IntPtr FindChild(IntPtr parent, string className, string? title = null)
    {
        return NativeMethods.FindWindowEx(parent, IntPtr.Zero, className, title);
    }

    private static Rect GetClientScreenBounds(IntPtr hwnd)
    {
        if (!NativeMethods.GetClientRect(hwnd, out var client))
        {
            return default;
        }

        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
        {
            return new Rect(0, 0, client.Right - client.Left, client.Bottom - client.Top);
        }

        return new Rect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            return Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
