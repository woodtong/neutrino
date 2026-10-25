using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace GhostScreen.Web;

public sealed class WebLaunchException : Exception
{
    public WebLaunchException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record WebLaunchOptions(
    string Url,
    int Width = 1280,
    int Height = 800,
    double DeviceScaleFactor = 1.0,
    bool Mobile = false,
    int TimeoutMs = 20_000);

/// <summary>
/// Owns one headless Edge process with a private profile. The browser is launched
/// with <c>--remote-debugging-port=0</c>; the real port is read from the
/// DevToolsActivePort file that Chromium writes into the profile directory.
/// </summary>
public sealed class EdgeBrowser : IAsyncDisposable
{
    private static readonly string[] EdgeCandidates =
    [
        @"Microsoft\Edge\Application\msedge.exe",
        @"Microsoft\Edge Beta\Application\msedge.exe",
        @"Microsoft\Edge Dev\Application\msedge.exe",
    ];

    private readonly Process _process;
    private bool _disposed;

    private EdgeBrowser(Process process, int port, string profileDirectory)
    {
        _process = process;
        Port = port;
        ProfileDirectory = profileDirectory;
    }

    public Process Process => _process;

    public int Port { get; }

    public string ProfileDirectory { get; }

    public int ProcessId => _process.Id;

    public static string? FindEdge() => CandidatePaths().FirstOrDefault(File.Exists);

    /// <summary>
    /// True only for a path built from the fixed install roots and Edge-relative
    /// candidates above. This is the allow-list checked before process creation.
    /// </summary>
    internal static bool IsKnownEdgePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return CandidatePaths().Any(candidate =>
                string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (var candidate in EdgeCandidates)
            {
                yield return Path.GetFullPath(Path.Combine(root, candidate));
            }
        }
    }

    public static async Task<EdgeBrowser> LaunchAsync(
        WebLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);

        var edgePath = FindEdge()
            ?? throw new WebLaunchException(
                "BROWSER_NOT_FOUND",
                "Microsoft Edge was not found in the standard install locations.");

        if (!IsKnownEdgePath(edgePath))
        {
            throw new WebLaunchException(
                "BROWSER_LAUNCH_FAILED",
                "Resolved Edge path is outside the known installation roots.");
        }

        var profileDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostScreen",
            "web-profiles",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(profileDirectory);

        if (profileDirectory.Contains('"', StringComparison.Ordinal) ||
            profileDirectory.Contains('\r', StringComparison.Ordinal) ||
            profileDirectory.Contains('\n', StringComparison.Ordinal))
        {
            throw new WebLaunchException(
                "BROWSER_LAUNCH_FAILED",
                "Profile path contains characters that cannot be quoted safely.");
        }

        // A user-supplied URL is never placed on the command line: the browser
        // always starts on about:blank and navigation goes through CDP. The
        // profile path is generated locally and quoted; no MCP parameter or URL
        // is concatenated into the command line.
        var commandLine =
            "msedge.exe --headless=new --remote-debugging-port=0 " +
            $"--user-data-dir=\"{profileDirectory}\" " +
            "--hide-scrollbars --no-first-run --no-default-browser-check --mute-audio " +
            "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding " +
            "--disable-background-timer-throttling --edge-skip-compat-layer-relaunch about:blank";

        Process process;
        try
        {
            process = NativeProcess.Start(edgePath, commandLine, profileDirectory);
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(profileDirectory);
            throw new WebLaunchException("BROWSER_LAUNCH_FAILED", $"Edge did not start: {ex.Message}");
        }

        var port = await WaitForPortAsync(profileDirectory, options.TimeoutMs, cancellationToken)
            .ConfigureAwait(false);

        if (port is null)
        {
            TryKill(process);
            TryDeleteDirectory(profileDirectory);
            throw new WebLaunchException(
                "BROWSER_LAUNCH_FAILED",
                "Edge did not expose a DevTools port within the timeout.");
        }

        return new EdgeBrowser(process, port.Value, profileDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await Task.Run(() => _process.WaitForExit(5_000)).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort shutdown.
        }

        _process.Dispose();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (TryDeleteDirectory(ProfileDirectory))
            {
                break;
            }

            await Task.Delay(150).ConfigureAwait(false);
        }
    }

    public static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new WebLaunchException("INVALID_ARGUMENTS", "url is required.");
        }

        var trimmed = url.Trim();

        if (trimmed.StartsWith('-'))
        {
            throw new WebLaunchException("INVALID_ARGUMENTS", "url must not start with '-'.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new WebLaunchException("INVALID_ARGUMENTS", $"Not an absolute URL: {trimmed}");
        }

        if (uri.Scheme is not ("http" or "https" or "file" or "about"))
        {
            throw new WebLaunchException(
                "INVALID_ARGUMENTS",
                $"Unsupported URL scheme '{uri.Scheme}'; use http, https, file, or about.");
        }

        return uri.AbsoluteUri;
    }

    private static void ValidateOptions(WebLaunchOptions options)
    {
        if (options.Width is < 64 or > 10_000 || options.Height is < 64 or > 10_000)
        {
            throw new WebLaunchException("INVALID_ARGUMENTS", "Viewport must be between 64 and 10000 pixels.");
        }

        if (options.DeviceScaleFactor is < 0.25 or > 4)
        {
            throw new WebLaunchException("INVALID_ARGUMENTS", "device_scale_factor must be between 0.25 and 4.");
        }

        if (options.TimeoutMs is < 1_000 or > 120_000)
        {
            throw new WebLaunchException("INVALID_ARGUMENTS", "timeout_ms must be between 1000 and 120000.");
        }
    }

    private static async Task<int?> WaitForPortAsync(
        string profileDirectory,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var portFile = Path.Combine(profileDirectory, "DevToolsActivePort");
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(portFile))
                {
                    var lines = await File.ReadAllLinesAsync(portFile, cancellationToken).ConfigureAwait(false);
                    if (lines.Length > 0 &&
                        int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) &&
                        port is > 0 and < 65_536)
                    {
                        return port;
                    }
                }
            }
            catch (IOException)
            {
                // Chromium is still writing the file.
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
        }
        catch
        {
            // Best effort.
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
