using System.Globalization;
using System.Text.Json;
using GhostScreen.Capture;
using GhostScreen.Core;
using GhostScreen.Windows;

DpiAwareness.EnablePerMonitorV2();
return Cli.Run(args);

internal static class Cli
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "version" => Version(),
            "state" => State(),
            "list" => List(args),
            "shot" => Shot(args),
            "help" or "-h" or "--help" => Help(),
            _ => Unknown(args[0]),
        };
    }

    private static int Version()
    {
        Console.WriteLine($"{ProductInfo.Name} {ProductInfo.Version}");
        return 0;
    }

    private static int State()
    {
        var display = DisplayInfo.Get();
        var state = SystemState.Capture();
        var foreground = WindowEnumerator.Describe(state.ForegroundWindow);
        var payload = new
        {
            display = new
            {
                virtualScreen = display.VirtualScreen.ToString(),
                primaryMonitor = display.PrimaryMonitor.ToString(),
            },
            system = new
            {
                foreground = state.ForegroundWindow.ToInt64().ToString("X"),
                foregroundWindow = foreground?.ToString(),
                cursor = new { x = state.CursorX, y = state.CursorY },
                clipboardSequence = state.ClipboardSequence,
                lastInputTick = state.LastInputTick,
            },
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        return 0;
    }

    private static int List(string[] args)
    {
        var includeInvisible = Has(args, "--all");
        var includeAll = includeInvisible;
        var json = Has(args, "--json");
        var windows = WindowEnumerator.List(includeInvisible: includeInvisible)
            .Where(window => includeAll ||
                             (!window.ToolWindow && !string.IsNullOrWhiteSpace(window.Title)))
            .ToArray();

        if (json)
        {
            var payload = windows.Select(window => new
            {
                handle = window.HexHandle,
                pid = window.ProcessId,
                process = window.ProcessName,
                title = window.Title,
                className = window.ClassName,
                bounds = window.Bounds.ToString(),
                visible = window.Visible,
                minimized = window.Minimized,
                cloaked = window.Cloaked,
                dpi = window.DpiScalePercent,
            });
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return 0;
        }

        Console.WriteLine($"{"HWND",-12} {"PID",-8} {"PROCESS",-22} {"DPI",-5} TITLE");
        foreach (var window in windows)
        {
            var title = window.Title.Length > 80 ? window.Title[..80] : window.Title;
            Console.WriteLine(
                $"{window.HexHandle,-12} {window.ProcessId,-8} {window.ProcessName,-22} " +
                $"{window.DpiScalePercent + "%",-5} {title}");
        }

        Console.WriteLine($"{windows.Length} window(s)");
        return 0;
    }

    private static int Shot(string[] args)
    {
        var hwndText = Get(args, "--hwnd");
        if (string.IsNullOrWhiteSpace(hwndText))
        {
            return Fail("USAGE", "shot requires --hwnd 0x...");
        }

        if (!TryParseHandle(hwndText, out var hwnd))
        {
            return Fail("INVALID_HWND", $"Cannot parse hwnd '{hwndText}'.");
        }

        if (!Has(args, "--confirm"))
        {
            return Fail(
                "CONFIRM_REQUIRED",
                "shot moves the window off-screen. Re-run with --confirm for a window you own or are authorized to move.");
        }

        var outputPath = Get(args, "--out")
            ?? Path.Combine(Path.GetTempPath(), "GhostScreen", $"shot-{hwnd.ToInt64():X}.png");
        var timeout = ParseInt(Get(args, "--timeout-ms"), 6_000);
        var waitStable = Has(args, "--no-stable") ? 0 : ParseInt(Get(args, "--stable-ms"), 250);

        var info = WindowEnumerator.Describe(hwnd);
        if (info is null)
        {
            return Fail("NOT_RENDERABLE", "Window handle is invalid or has no bounds.");
        }

        try
        {
            using var parked = ParkingManager.Park(hwnd);
            WindowRepaint.Force(hwnd);
            Thread.Sleep(200);
            WindowRepaint.Force(hwnd);

            var result = CaptureEngine.Capture(
                hwnd,
                new CaptureOptions(timeout, waitStable));

            PngWriter.SaveBgraAsPng(
                result.Frame.Bgra,
                result.Frame.Width,
                result.Frame.Height,
                outputPath);

            var payload = new
            {
                ok = true,
                target = new
                {
                    handle = info.HexHandle,
                    pid = info.ProcessId,
                    process = info.ProcessName,
                    title = info.Title,
                    movedFrom = parked.OriginalBounds.ToString(),
                    parkedAt = parked.ParkedBounds.ToString(),
                },
                capture = new
                {
                    backend = result.Metadata.Backend,
                    width = result.Metadata.Width,
                    height = result.Metadata.Height,
                    stable = result.Metadata.Stable,
                    blank = result.Metadata.Blank,
                    hash = result.Metadata.Hash,
                    capturedAtUnixMs = result.Metadata.CapturedAtUnixMs,
                    window = result.Metadata.WindowRect.ToString(),
                    client = result.Metadata.ClientRect.ToString(),
                    clientOrigin = new { x = result.Metadata.ClientOriginX, y = result.Metadata.ClientOriginY },
                    dpi = result.Metadata.DpiScalePercent,
                },
                file = Path.GetFullPath(outputPath),
            };

            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return 0;
        }
        catch (CaptureException ex)
        {
            return Fail(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return Fail("INTERNAL", ex.Message);
        }
    }

    private static int Help()
    {
        PrintHelp();
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 2;
    }

    private static int Fail(string code, string message)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error = new { code, message } }, JsonOptions));
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            GhostScreen CLI (P1)

              gs version
              gs state
              gs list [--all] [--json]
              gs shot --hwnd 0x... [--out path] [--confirm] [--no-stable] [--timeout-ms N]

            shot requires --confirm because it moves the window off-screen and restores it.
            """);
    }

    private static bool Has(string[] args, string name) =>
        args.Any(argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Get(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool TryParseHandle(string text, out IntPtr handle)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        if (long.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            handle = new IntPtr(value);
            return true;
        }

        handle = IntPtr.Zero;
        return false;
    }
}
