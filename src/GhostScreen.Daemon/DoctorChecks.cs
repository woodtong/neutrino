using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using GhostScreen.Capture;
using GhostScreen.Core;
using GhostScreen.Web;
using GhostScreen.Windows;

namespace GhostScreen.Daemon;

public enum DoctorStatus
{
    Ok,
    Warn,
    Fail,
    Info,
}

public sealed record DoctorItem(string Id, DoctorStatus Status, string Detail, string? Hint = null);

/// <summary>
/// Environment diagnostics for `gs doctor` (PLAN P2-07). Every check returns a
/// status, a human-readable detail, and — when something is off — a hint with
/// the next command or doc link. Nothing here mutates state.
/// </summary>
public static partial class DoctorChecks
{
    public static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GhostScreen");

    public static List<DoctorItem> Run()
    {
        var items = new List<DoctorItem>
        {
            CheckOs(),
            CheckEdge(),
            CheckWgc(),
            CheckDisplay(),
            CheckStateDirectory(),
            CheckStatusEndpoint(),
        };
        return items;
    }

    private static DoctorItem CheckOs()
    {
        var version = Environment.OSVersion.Version;
        var minimum = new Version(10, 0, 19041);
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var detail = $"Windows {version} ({Environment.OSVersion.Platform}, {arch})";
        if (arch != System.Runtime.InteropServices.Architecture.X64)
        {
            return new DoctorItem(
                "os",
                DoctorStatus.Warn,
                detail,
                "Only x64 is built today; ARM64 is out of scope for v1.0 (see PLAN P5).");
        }

        return version >= minimum
            ? new DoctorItem("os", DoctorStatus.Ok, detail)
            : new DoctorItem(
                "os",
                DoctorStatus.Fail,
                detail,
                $"GhostScreen requires Windows 10 19041+ / Windows 11. See {DocLink("compatibility")}.");
    }

    private static DoctorItem CheckEdge()
    {
        var path = EdgeBrowser.FindEdge();
        if (path is null)
        {
            return new DoctorItem(
                "edge",
                DoctorStatus.Warn,
                "Microsoft Edge not found in the standard install locations.",
                "open_web/attach_cdp targets are unavailable. Install Edge or see doc/12-Recipes.md for other browsers.");
        }

        string versionText;
        try
        {
            versionText = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "unknown";
        }
        catch
        {
            versionText = "unknown";
        }

        return new DoctorItem("edge", DoctorStatus.Ok, $"{path} ({versionText})");
    }

    private static DoctorItem CheckWgc()
    {
        var supported = WgcCapture.IsSupported();
        return supported
            ? new DoctorItem("wgc", DoctorStatus.Ok, "Windows.Graphics.Capture available (fallback for visible windows).")
            : new DoctorItem(
                "wgc",
                DoctorStatus.Warn,
                "Windows.Graphics.Capture is not available.",
                "PrintWindow remains the main capture path and works without WGC; visible-window fallback and VDD are disabled.");
    }

    private static DoctorItem CheckDisplay()
    {
        try
        {
            var display = DisplayInfo.Get();
            if (display.VirtualScreen.Width <= 0 || display.VirtualScreen.Height <= 0)
            {
                return new DoctorItem(
                    "display",
                    DoctorStatus.Fail,
                    $"Virtual screen reported empty: {display.VirtualScreen}.",
                    "Attach a monitor or run on an interactive desktop session (RDP disconnected sessions have no display).");
            }

            return new DoctorItem(
                "display",
                DoctorStatus.Ok,
                $"Virtual screen {display.VirtualScreen}; primary {display.PrimaryMonitor}; park position exists to the right of the virtual screen.");
        }
        catch (Exception ex)
        {
            return new DoctorItem("display", DoctorStatus.Fail, $"Display enumeration failed: {ex.Message}", DocLink("troubleshooting"));
        }
    }

    private static DoctorItem CheckStateDirectory()
    {
        if (!Directory.Exists(StateDirectory))
        {
            return new DoctorItem(
                "state_dir",
                DoctorStatus.Info,
                $"{StateDirectory} does not exist yet.",
                "Created on first MCP run; nothing to recover.");
        }

        var journal = TargetJournal.DefaultPath;
        if (File.Exists(journal))
        {
            try
            {
                using var _ = JsonDocument.Parse(File.ReadAllText(journal));
                return new DoctorItem("state_dir", DoctorStatus.Ok, $"Journal {journal} parses cleanly.");
            }
            catch (Exception ex)
            {
                return new DoctorItem(
                    "state_dir",
                    DoctorStatus.Warn,
                    $"Journal {journal} is corrupt ({ex.GetType().Name}); startup moves it to .corrupt-* and continues.",
                    "No action needed unless windows were parked during a crash; see doc/16-技术实现与限制报告.md on recovery.");
            }
        }

        return new DoctorItem("state_dir", DoctorStatus.Ok, $"{StateDirectory} present, no pending journal entries.");
    }

    private static DoctorItem CheckStatusEndpoint()
    {
        var endpoint = StatusClient.TryReadEndpoint();
        if (endpoint is null)
        {
            var file = StatusClient.DefaultEndpointPath;
            return File.Exists(file)
                ? new DoctorItem(
                    "status",
                    DoctorStatus.Warn,
                    $"{file} exists but is invalid (bad JSON, missing token, or not 127.0.0.1).",
                    "Delete the file and restart the MCP server; it is rewritten on every startup.")
                : new DoctorItem(
                    "status",
                    DoctorStatus.Info,
                    "No MCP server running (no status.json).",
                    "This is normal when the MCP server is not started by a client.");
        }

        try
        {
            using var client = new StatusClient(endpoint);
            var snapshot = client.GetStatusAsync().GetAwaiter().GetResult();
            if (snapshot is null)
            {
                return new DoctorItem(
                    "status",
                    DoctorStatus.Warn,
                    $"Endpoint file present ({SanitizeUrl(endpoint.Url)}) but /api/status is unreachable.",
                    "The MCP process may have crashed without cleanup; delete status.json and reconnect the client.");
            }

            return new DoctorItem(
                "status",
                DoctorStatus.Ok,
                $"MCP v{snapshot.Version} pid {snapshot.Pid} up {snapshot.UptimeSeconds}s, {snapshot.TargetCount} target(s).");
        }
        catch (Exception ex)
        {
            return new DoctorItem("status", DoctorStatus.Warn, $"Status poll failed: {ex.Message}", DocLink("troubleshooting"));
        }
    }

    private static string DocLink(string anchor) => "doc/19-Troubleshooting.md" + (anchor is null ? "" : $"#{anchor}");

    /// <summary>Strips the token query from a status URL so it can be printed safely.</summary>
    public static string SanitizeUrl(string url)
    {
        var questionIndex = url.IndexOf('?');
        return questionIndex >= 0 ? url[..questionIndex] + "?token=%REDACTED%" : url;
    }

    [GeneratedRegex(@"(?i)([A-Za-z]:\\Users\\)[^\\\s""]+")]
    private static partial Regex UserNameRegex();

    [GeneratedRegex(@"(?i)token=[^&\s""]+")]
    private static partial Regex TokenRegex();

    /// <summary>
    /// Redacts a value for the `--bundle` report: status tokens (in URLs or bare)
    /// and the local username inside file paths.
    /// </summary>
    public static string Redact(string value)
    {
        var userName = Environment.UserName;
        var result = UserNameRegex().Replace(value, "$1%USER%");
        result = TokenRegex().Replace(result, "token=%REDACTED%");
        if (!string.IsNullOrEmpty(userName))
        {
            result = result.Replace(userName, "%USER%", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    public static string BuildBundle(IEnumerable<DoctorItem> items)
    {
        var lines = new List<string>
        {
            $"GhostScreen doctor bundle — {ProductInfo.Name} {ProductInfo.Version}",
            $"Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} local",
            string.Empty,
        };
        lines.AddRange(items.Select(item =>
            $"[{item.Status.ToString().ToUpperInvariant()}] {item.Id}: {Redact(item.Detail)}" +
            (item.Hint is null ? string.Empty : $"\n       hint: {Redact(item.Hint)}")));
        return string.Join(Environment.NewLine, lines);
    }
}
