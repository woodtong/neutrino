using System.Globalization;
using System.Text;
using System.Text.Json;
using GhostScreen.Capture;
using GhostScreen.Core;
using GhostScreen.Daemon;
using GhostScreen.Web;
using GhostScreen.Windows;

DpiAwareness.EnablePerMonitorV2();
using var manager = new TargetManager();

if (!manager.LastRecovery.IsEmpty)
{
    Console.Error.WriteLine($"GhostScreen recovery: {manager.LastRecovery}");
    foreach (var message in manager.LastRecovery.Messages)
    {
        Console.Error.WriteLine($"  {message}");
    }
}

using var status = new StatusServer(manager);
status.Start();
Console.Error.WriteLine($"GhostScreen status page: {status.Url}");

await McpServer.RunStdioAsync(manager);

public sealed class McpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly TargetManager _manager;
    private readonly TextReader _reader;
    private readonly TextWriter _writer;

    public McpServer(TargetManager manager, TextReader reader, TextWriter writer)
    {
        _manager = manager;
        _reader = reader;
        _writer = writer;
    }

    public static async Task RunStdioAsync(TargetManager manager)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => manager.CloseAll();
        Console.CancelKeyPress += (_, _) => manager.CloseAll();
        Console.Error.WriteLine($"GhostScreen MCP started (pid {Environment.ProcessId}).");

        try
        {
            await new McpServer(manager, reader, writer).RunAsync();
        }
        finally
        {
            Console.Error.WriteLine("GhostScreen MCP stopped; all targets restored.");
        }
    }

    public async Task RunAsync()
    {
        try
        {
            string? line;
            while ((line = await _reader.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                await HandleLineAsync(line);
            }
        }
        finally
        {
            _manager.CloseAll();
        }
    }

    private async Task HandleLineAsync(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(default, -32700, $"Parse error: {ex.Message}");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var methodElement))
            {
                return;
            }

            var method = methodElement.GetString() ?? string.Empty;
            var hasId = root.TryGetProperty("id", out var id);
            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement
                : default;

            if (!hasId)
            {
                return;
            }

            try
            {
                switch (method)
                {
                    case "initialize":
                        await WriteResultAsync(id, Initialize(parameters));
                        break;
                    case "ping":
                        await WriteResultAsync(id, new { });
                        break;
                    case "tools/list":
                        await WriteResultAsync(id, new { tools = BuildTools() });
                        break;
                    case "tools/call":
                        await WriteResultAsync(id, await CallToolAsync(parameters));
                        break;
                    default:
                        await WriteErrorAsync(id, -32601, $"Method not found: {method}");
                        break;
                }
            }
            catch (TargetException ex)
            {
                await WriteResultAsync(id, ErrorResult(ex.Code, ex.Message));
            }
            catch (CaptureException ex)
            {
                await WriteResultAsync(id, ErrorResult(ex.Code, ex.Message));
            }
            catch (ArgumentException ex)
            {
                await WriteResultAsync(id, ErrorResult("INVALID_ARGUMENTS", ex.Message));
            }
            catch (TimeoutException ex)
            {
                await WriteResultAsync(id, ErrorResult("TIMEOUT", ex.Message));
            }
            catch (Exception ex)
            {
                await WriteResultAsync(id, ErrorResult("INTERNAL", ex.Message));
            }
        }
    }

    private static object Initialize(JsonElement parameters)
    {
        var requested = parameters.ValueKind == JsonValueKind.Object &&
                        parameters.TryGetProperty("protocolVersion", out var version)
            ? version.GetString()
            : null;

        return new
        {
            protocolVersion = string.IsNullOrWhiteSpace(requested) ? "2024-11-05" : requested,
            capabilities = new
            {
                tools = new { },
            },
            serverInfo = new
            {
                name = ProductInfo.Name,
                version = ProductInfo.Version,
            },
            instructions =
                "Local screen isolation for agents. Native apps: attach_window (confirm=true) then screenshot/observe/click. Web: open_web with a URL (private headless Edge, no window). Targets are restored/closed on close_target, close_all, or EOF. Nothing is sent to the cloud.",
        };
    }

    private async Task<object> CallToolAsync(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out var nameElement))
        {
            return ErrorResult("INVALID_ARGUMENTS", "tools/call requires a name.");
        }

        var name = nameElement.GetString() ?? string.Empty;
        var args = parameters.TryGetProperty("arguments", out var arguments) &&
                   arguments.ValueKind == JsonValueKind.Object
            ? arguments
            : default;

        return name switch
        {
            "list_windows" => ListWindows(args),
            "attach_window" => AttachWindow(args),
            "open_web" => await OpenWebAsync(args),
            "attach_cdp" => await AttachCdpAsync(args),
            "list_targets" => ListTargets(),
            "screenshot" => await ScreenshotAsync(args),
            "snapshot" => await SnapshotAsync(args),
            "observe" => await ObserveAsync(args),
            "click" => await ClickAsync(args),
            "click_element" => await ClickElementAsync(args),
            "scroll" => await ScrollAsync(args),
            "drag" => await DragAsync(args),
            "key" => await KeyAsync(args),
            "set_text" => await SetTextAsync(args),
            "navigate" => await NavigateAsync(args),
            "viewport" => await ViewportAsync(args),
            "console" => ConsoleEntries(args),
            "network" => NetworkIssues(args),
            "get_html" => await GetHtmlAsync(args),
            "close_target" => CloseTarget(args),
            "close_all" => CloseAll(),
            _ => ErrorResult("UNKNOWN_TOOL", $"Unknown tool: {name}"),
        };
    }

    private object ListWindows(JsonElement args)
    {
        var includeInvisible = GetBool(args, "include_invisible", false);
        var includeOwn = GetBool(args, "include_own_process", false);
        var windows = WindowEnumerator.List(
                includeInvisible: includeInvisible,
                includeOwnProcess: includeOwn)
            .Where(window => !window.ToolWindow && !string.IsNullOrWhiteSpace(window.Title))
            .Select(window => new
            {
                hwnd = window.HexHandle,
                pid = window.ProcessId,
                process = window.ProcessName,
                title = window.Title,
                className = window.ClassName,
                bounds = window.Bounds.ToString(),
                dpi = window.DpiScalePercent,
                dpi_awareness = window.DpiAwareness.ToString(),
            })
            .ToArray();

        return TextResult(JsonSerializer.Serialize(windows, JsonOptions));
    }

    private object AttachWindow(JsonElement args)
    {
        var confirm = GetBool(args, "confirm", false);
        var hwndText = GetString(args, "hwnd");
        TargetSession session;

        if (!string.IsNullOrWhiteSpace(hwndText))
        {
            var hwnd = ParseHandle(hwndText);
            session = _manager.Attach(hwnd, confirm);
        }
        else
        {
            var title = GetString(args, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                return ErrorResult("INVALID_ARGUMENTS", "attach_window requires hwnd or title.");
            }

            session = _manager.AttachByTitle(title, confirm);
        }

        return TextResult(JsonSerializer.Serialize(session.ToDto(), JsonOptions));
    }

    private object ListTargets()
    {
        var targets = _manager.List().Select(session => session.ToDto()).ToArray();
        return TextResult(JsonSerializer.Serialize(targets, JsonOptions));
    }

    private async Task<object> ScreenshotAsync(JsonElement args)
    {
        var target = GetTarget(args);
        var fullPage = GetBool(args, "full_page", false);
        var clip = ParseClip(args);

        if (target is not WebTargetSession && (fullPage || clip is not null))
        {
            return ErrorResult("TARGET_KIND_MISMATCH", "full_page and clip are web-only; native screenshots use the client area.");
        }

        if (target is WebTargetSession web)
        {
            var webCapture = await web.CaptureAsync(fullPage, clip);
            var webMetadata = new
            {
                target_id = web.Id,
                kind = web.Kind,
                backend = "cdp",
                region = webCapture.Region,
                clip = webCapture.Clip is null
                    ? null
                    : new
                    {
                        x = webCapture.Clip.X,
                        y = webCapture.Clip.Y,
                        width = webCapture.Clip.Width,
                        height = webCapture.Clip.Height,
                    },
                width = webCapture.Width,
                height = webCapture.Height,
                css_width = webCapture.CssWidth,
                css_height = webCapture.CssHeight,
                image_scale = webCapture.DeviceScaleFactor,
                stable = true,
                blank = false,
                hash = webCapture.Hash,
                captured_at = webCapture.CapturedAtUnixMs,
                url = webCapture.Url,
                title = webCapture.Title,
            };

            return new
            {
                content = new object[]
                {
                    Text(JsonSerializer.Serialize(webMetadata, JsonOptions)),
                    Image(webCapture.Png),
                },
                isError = false,
            };
        }

        var session = (TargetSession)target;
        var timeout = GetInt(args, "timeout_ms", 6_000);
        var waitStable = GetInt(args, "wait_stable_ms", 250);
        var result = session.Capture(new CaptureOptions(timeout, waitStable));
        var png = PngWriter.EncodeBgraAsPng(
            result.Frame.Bgra,
            result.Frame.Width,
            result.Frame.Height);

        var metadata = new
        {
            target_id = session.Id,
            kind = session.Kind,
            backend = result.Metadata.Backend,
            width = result.Metadata.Width,
            height = result.Metadata.Height,
            stable = result.Metadata.Stable,
            blank = result.Metadata.Blank,
            hash = result.Metadata.Hash,
            captured_at = result.Metadata.CapturedAtUnixMs,
            window = result.Metadata.WindowRect.ToString(),
            client = result.Metadata.ClientRect.ToString(),
            client_origin = new { x = result.Metadata.ClientOriginX, y = result.Metadata.ClientOriginY },
            dpi = result.Metadata.DpiScalePercent,
        };

        return new
        {
            content = new object[]
            {
                Text(JsonSerializer.Serialize(metadata, JsonOptions)),
                Image(png),
            },
            isError = false,
        };
    }

    private async Task<object> SnapshotAsync(JsonElement args)
    {
        var target = GetTarget(args);
        if (target is not WebTargetSession web)
        {
            return ErrorResult("TARGET_KIND_MISMATCH", "snapshot is web-only; use screenshot for native windows.");
        }

        var specs = new List<ViewportSpec>();
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("viewports", out var viewportsElement) &&
            viewportsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in viewportsElement.EnumerateArray())
            {
                var width = GetInt(item, "width", 0);
                var height = GetInt(item, "height", 0);
                if (width is < 64 or > 10_000 || height is < 64 or > 10_000)
                {
                    return ErrorResult(
                        "INVALID_ARGUMENTS",
                        "Each viewport needs width/height between 64 and 10000.");
                }

                specs.Add(new ViewportSpec(
                    width,
                    height,
                    Math.Clamp(GetDouble(item, "device_scale_factor", 1), 0.25, 4.0),
                    GetBool(item, "mobile", false)));
            }
        }

        if (specs.Count == 0)
        {
            specs.Add(new ViewportSpec(390, 844, 2, true));
            specs.Add(new ViewportSpec(768, 1024, 1, false));
            specs.Add(new ViewportSpec(1440, 900, 1, false));
        }

        if (specs.Count > 6)
        {
            return ErrorResult("INVALID_ARGUMENTS", "At most 6 viewports per snapshot.");
        }

        var fullPage = GetBool(args, "full_page", false);
        var shots = await web.SnapshotAsync(specs, fullPage);

        var summary = new
        {
            target_id = web.Id,
            kind = web.Kind,
            full_page = fullPage,
            shots = shots.Select(shot => new
            {
                index = shot.Index,
                viewport = new
                {
                    width = shot.Viewport.Width,
                    height = shot.Viewport.Height,
                    device_scale_factor = shot.Viewport.DeviceScaleFactor,
                    mobile = shot.Viewport.Mobile,
                },
                width = shot.Capture.Width,
                height = shot.Capture.Height,
                css_width = shot.Capture.CssWidth,
                css_height = shot.Capture.CssHeight,
                hash = shot.Capture.Hash,
                region = shot.Capture.Region,
                overflow = new
                {
                    inner_width = shot.Overflow.InnerWidth,
                    scroll_width = shot.Overflow.ScrollWidth,
                    horizontal_overflow = shot.Overflow.HorizontalOverflow,
                    offenders = shot.Overflow.Offenders.Select(offender => new
                    {
                        selector = offender.Selector,
                        x = offender.X,
                        y = offender.Y,
                        width = offender.Width,
                        height = offender.Height,
                        right = offender.Right,
                    }).ToArray(),
                },
            }).ToArray(),
        };

        var content = new List<object> { Text(JsonSerializer.Serialize(summary, JsonOptions)) };
        foreach (var shot in shots)
        {
            content.Add(Image(shot.Capture.Png));
        }

        return new { content = content.ToArray(), isError = false };
    }

    private async Task<object> ObserveAsync(JsonElement args)
    {
        var target = GetTarget(args);
        var maxNodes = GetInt(args, "max_nodes", 100);

        if (target is WebTargetSession web)
        {
            var webObservation = await web.ObserveAsync(maxNodes);
            var webPayload = new
            {
                kind = web.Kind,
                epoch = webObservation.Epoch,
                elements = webObservation.Elements.Select(element => new
                {
                    refId = element.Ref,
                    tag = element.Tag,
                    role = element.Role,
                    name = element.Name,
                    selector = element.Selector,
                    bounds = element.Bounds.ToString(),
                    enabled = element.Enabled,
                }).ToArray(),
            };

            return TextResult(JsonSerializer.Serialize(webPayload, JsonOptions));
        }

        var session = (TargetSession)target;
        var observation = session.Observe(maxNodes);
        var payload = new
        {
            kind = session.Kind,
            epoch = observation.Epoch,
            elements = observation.Elements.Select(element => new
            {
                refId = element.Ref,
                role = element.Role,
                name = element.Name,
                automation_id = element.AutomationId,
                bounds = element.Bounds.ToString(),
                enabled = element.Enabled,
                focused = element.Focused,
                patterns = element.Patterns,
            }).ToArray(),
        };

        return TextResult(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private async Task<object> ClickAsync(JsonElement args)
    {
        var target = GetTarget(args);
        var x = GetInt(args, "x", -1);
        var y = GetInt(args, "y", -1);
        if (x < 0 || y < 0)
        {
            return ErrorResult("INVALID_ARGUMENTS", "click requires x and y in client-area pixels.");
        }

        if (target is WebTargetSession web)
        {
            var webOk = await web.ClickPointAsync(x, y);
            return TextResult(JsonSerializer.Serialize(
                new { ok = webOk, strategy = "cdp", space = "css" },
                JsonOptions));
        }

        var strictNoFocusSteal = GetBool(args, "strict_no_focus_steal", false);
        var pointResult = ((TargetSession)target).ClickPoint(x, y, strictNoFocusSteal);
        return NativeResult(pointResult, "client");
    }

    private async Task<object> ClickElementAsync(JsonElement args)
    {
        var target = GetTarget(args);
        var reference = GetString(args, "ref");
        var name = GetString(args, "name");

        if (target is WebTargetSession web)
        {
            if (!string.IsNullOrWhiteSpace(reference))
            {
                var webOkRef = await web.ClickRefAsync(reference);
                return TextResult(JsonSerializer.Serialize(new { ok = webOkRef, strategy = "cdp-ref" }, JsonOptions));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return ErrorResult("INVALID_ARGUMENTS", "click_element requires ref or name.");
            }

            var webOkName = await web.ClickNameAsync(name);
            return TextResult(JsonSerializer.Serialize(new { ok = webOkName, strategy = "cdp-name" }, JsonOptions));
        }

        var session = (TargetSession)target;
        var strictNoFocusSteal = GetBool(args, "strict_no_focus_steal", false);

        if (!string.IsNullOrWhiteSpace(reference))
        {
            var okRef = session.ClickRef(reference, strictNoFocusSteal);
            return NativeResult(okRef);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return ErrorResult("INVALID_ARGUMENTS", "click_element requires ref or name.");
        }

        var ok = session.ClickElement(name, strictNoFocusSteal);
        return NativeResult(ok);
    }

    private async Task<object> ScrollAsync(JsonElement args)
    {
        var target = GetTarget(args);
        var x = GetInt(args, "x", -1);
        var y = GetInt(args, "y", -1);
        var delta = GetInt(args, "delta", 120);
        if (x < 0 || y < 0 || delta == 0)
        {
            return ErrorResult("INVALID_ARGUMENTS", "scroll requires x, y, and a non-zero delta.");
        }

        if (target is WebTargetSession web)
        {
            var webOk = await web.ScrollAsync(x, y, delta);
            return TextResult(JsonSerializer.Serialize(new { ok = webOk, strategy = "cdp" }, JsonOptions));
        }

        var ok = ((TargetSession)target).Scroll(x, y, delta);
        return TextResult(JsonSerializer.Serialize(new { ok, strategy = "message" }, JsonOptions));
    }

    private async Task<object> DragAsync(JsonElement args)
    {
        var target = GetTarget(args);
        var fromX = GetInt(args, "from_x", -1);
        var fromY = GetInt(args, "from_y", -1);
        var toX = GetInt(args, "to_x", -1);
        var toY = GetInt(args, "to_y", -1);
        if (fromX < 0 || fromY < 0 || toX < 0 || toY < 0)
        {
            return ErrorResult("INVALID_ARGUMENTS", "drag requires from_x/from_y/to_x/to_y.");
        }

        var duration = GetInt(args, "duration_ms", 300);

        if (target is WebTargetSession web)
        {
            var webOk = await web.DragAsync(fromX, fromY, toX, toY, duration);
            return TextResult(JsonSerializer.Serialize(new { ok = webOk, strategy = "cdp" }, JsonOptions));
        }

        var ok = ((TargetSession)target).Drag(fromX, fromY, toX, toY, duration);
        return TextResult(JsonSerializer.Serialize(new { ok, strategy = "message" }, JsonOptions));
    }

    private async Task<object> KeyAsync(JsonElement args)
    {
        var target = GetTarget(args);
        var keys = new List<string>();

        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("keys", out var keysElement))
        {
            if (keysElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in keysElement.EnumerateArray())
                {
                    var key = item.GetString();
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        keys.Add(key);
                    }
                }
            }
            else if (keysElement.ValueKind == JsonValueKind.String)
            {
                keys.AddRange(
                    (keysElement.GetString() ?? string.Empty)
                    .Split(['+', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        if (keys.Count == 0)
        {
            return ErrorResult("INVALID_ARGUMENTS", "key requires a non-empty keys array, e.g. [\"CTRL\",\"S\"].");
        }

        if (target is WebTargetSession web)
        {
            var webOk = await web.PressKeyAsync(keys);
            return TextResult(JsonSerializer.Serialize(new { ok = webOk, strategy = "cdp", keys }, JsonOptions));
        }

        var ok = ((TargetSession)target).PressKey(keys);
        return TextResult(JsonSerializer.Serialize(new { ok, strategy = "message", keys }, JsonOptions));
    }

    private async Task<object> SetTextAsync(JsonElement args)
    {
        var target = GetTarget(args);
        var text = GetString(args, "text") ?? string.Empty;

        if (target is WebTargetSession web)
        {
            var reference = GetString(args, "ref");
            if (string.IsNullOrWhiteSpace(reference))
            {
                return ErrorResult("INVALID_ARGUMENTS", "set_text on a web target requires ref from observe.");
            }

            var webOk = await web.SetTextByRefAsync(reference, text);
            return TextResult(JsonSerializer.Serialize(new { ok = webOk, strategy = "cdp" }, JsonOptions));
        }

        var automationId = GetString(args, "automation_id");
        if (string.IsNullOrWhiteSpace(automationId))
        {
            return ErrorResult("INVALID_ARGUMENTS", "set_text requires automation_id.");
        }

        var strictNoFocusSteal = GetBool(args, "strict_no_focus_steal", false);
        var ok = ((TargetSession)target).SetText(automationId, text, strictNoFocusSteal);
        return NativeResult(ok);
    }

    private async Task<object> OpenWebAsync(JsonElement args)
    {
        var url = GetString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return ErrorResult("INVALID_ARGUMENTS", "open_web requires url.");
        }

        var options = new WebLaunchOptions(
            url,
            Math.Clamp(GetInt(args, "width", 1280), 64, 10_000),
            Math.Clamp(GetInt(args, "height", 800), 64, 10_000),
            Math.Clamp(GetDouble(args, "device_scale_factor", 1.0), 0.25, 4.0),
            GetBool(args, "mobile", false),
            Math.Clamp(GetInt(args, "timeout_ms", 20_000), 1_000, 120_000));

        var session = await _manager.OpenWebAsync(options);
        return TextResult(JsonSerializer.Serialize(session.ToDto(), JsonOptions));
    }

    private async Task<object> AttachCdpAsync(JsonElement args)
    {
        var port = GetInt(args, "port", 0);
        if (port is < 1 or > 65_535)
        {
            return ErrorResult("INVALID_ARGUMENTS", "attach_cdp requires port between 1 and 65535.");
        }

        var session = await _manager.AttachCdpAsync(port, GetString(args, "page_url"));
        return TextResult(JsonSerializer.Serialize(session.ToDto(), JsonOptions));
    }

    private async Task<object> NavigateAsync(JsonElement args)
    {
        var session = _manager.GetWeb(GetTargetId(args));
        var url = GetString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return ErrorResult("INVALID_ARGUMENTS", "navigate requires url.");
        }

        await session.NavigateAsync(url);
        return TextResult(JsonSerializer.Serialize(
            new { ok = true, url = session.Url, title = session.Title },
            JsonOptions));
    }

    private async Task<object> ViewportAsync(JsonElement args)
    {
        var session = _manager.GetWeb(GetTargetId(args));
        var width = GetInt(args, "width", session.CssWidth);
        var height = GetInt(args, "height", session.CssHeight);
        if (width is < 64 or > 10_000 || height is < 64 or > 10_000)
        {
            return ErrorResult("INVALID_ARGUMENTS", "viewport must be between 64 and 10000 pixels.");
        }

        await session.SetViewportAsync(
            width,
            height,
            Math.Clamp(GetDouble(args, "device_scale_factor", session.DeviceScaleFactor), 0.25, 4.0),
            GetBool(args, "mobile", session.Mobile));

        return TextResult(JsonSerializer.Serialize(
            new
            {
                ok = true,
                viewport = new
                {
                    width = session.CssWidth,
                    height = session.CssHeight,
                    device_scale_factor = session.DeviceScaleFactor,
                    mobile = session.Mobile,
                },
            },
            JsonOptions));
    }

    private object ConsoleEntries(JsonElement args)
    {
        var session = _manager.GetWeb(GetTargetId(args));
        var limit = Math.Clamp(GetInt(args, "limit", 50), 1, 1_000);
        var entries = session.ConsoleEntries
            .TakeLast(limit)
            .Select(entry => new { level = entry.Level, text = entry.Text })
            .ToArray();

        return TextResult(JsonSerializer.Serialize(new { count = entries.Length, entries }, JsonOptions));
    }

    private object NetworkIssues(JsonElement args)
    {
        var session = _manager.GetWeb(GetTargetId(args));
        var limit = Math.Clamp(GetInt(args, "limit", 50), 1, 200);
        var issues = session.NetworkIssues
            .TakeLast(limit)
            .Select(issue => new
            {
                kind = issue.Kind,
                url = issue.Url,
                method = issue.Method,
                status = issue.Status,
                error = issue.Error,
                resource_type = issue.ResourceType,
            })
            .ToArray();

        return TextResult(JsonSerializer.Serialize(new { count = issues.Length, issues }, JsonOptions));
    }

    private async Task<object> GetHtmlAsync(JsonElement args)
    {
        var session = _manager.GetWeb(GetTargetId(args));
        var selector = GetString(args, "selector");
        var maxChars = Math.Clamp(GetInt(args, "max_chars", 200_000), 1_000, 1_000_000);
        var html = await session.GetHtmlAsync(selector, maxChars);

        if (html is null)
        {
            return ErrorResult("ELEMENT_NOT_FOUND", $"No element matches '{selector}'.");
        }

        return TextResult(JsonSerializer.Serialize(
            new { length = html.Length, html },
            JsonOptions));
    }

    private object CloseTarget(JsonElement args)
    {
        var target = GetTarget(args);
        var ok = _manager.Close(target.Id);
        return TextResult(JsonSerializer.Serialize(new { ok, target_id = target.Id }, JsonOptions));
    }

    private object CloseAll()
    {
        var count = _manager.List().Count;
        _manager.CloseAll();
        return TextResult(JsonSerializer.Serialize(new { ok = true, closed = count }, JsonOptions));
    }

    private ITarget GetTarget(JsonElement args) => _manager.Get(GetTargetId(args));

    private static string GetTargetId(JsonElement args)
    {
        var targetId = GetString(args, "target_id");
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new TargetException("INVALID_ARGUMENTS", "target_id is required.");
        }

        return targetId;
    }

    private static object[] BuildTools() =>
    [
        Tool(
            "list_windows",
            "List visible top-level windows that can be attached. Returns hwnd/pid/process/title/bounds/dpi.",
            Props(
                ("include_invisible", "boolean", "Include hidden/cloaked windows."),
                ("include_own_process", "boolean", "Include windows owned by this process (mainly for tests).")),
            []),
        Tool(
            "attach_window",
            "Park an existing window off-screen and return a target_id. Moves the window; confirm must be true.",
            Props(
                ("hwnd", "string", "Window handle (hex, e.g. 0x1234)."),
                ("title", "string", "Unique title substring; alternative to hwnd."),
                ("confirm", "boolean", "Must be true to move the window.")),
            ["confirm"]),
        Tool(
            "open_web",
            "Open a URL in a private headless Edge (no window, no user profile) and return a target_id. Supports http/https/file URLs.",
            Props(
                ("url", "string", "Absolute URL to open."),
                ("width", "integer", "Viewport width in CSS pixels; default 1280."),
                ("height", "integer", "Viewport height in CSS pixels; default 800."),
                ("device_scale_factor", "number", "Device scale factor; default 1."),
                ("mobile", "boolean", "Emulate a mobile viewport."),
                ("timeout_ms", "integer", "Launch/navigation timeout; default 20000.")),
            ["url"]),
        Tool(
            "attach_cdp",
            "Attach to an existing Chromium/Electron DevTools endpoint (e.g. VS Code with --remote-debugging-port). Does not launch or close the browser; close_target detaches.",
            Props(
                ("port", "integer", "DevTools port of the running app."),
                ("page_url", "string", "Optional URL substring to pick a page target.")),
            ["port"]),
        Tool("list_targets", "List currently attached targets (native windows and web pages).", Props(), []),
        Tool(
            "screenshot",
            "Capture the target as a PNG plus metadata. Native: PrintWindow/WGC of the client area. Web: CDP viewport, full_page, or clip (CSS pixels).",
            Props(
                ("target_id", "string", "Target id."),
                ("timeout_ms", "integer", "Capture timeout for native targets; default 6000."),
                ("wait_stable_ms", "integer", "Wait for a stable native frame; default 250, 0 disables."),
                ("full_page", "boolean", "Web only: capture the full scrollable page."),
                ("clip", "object", "Web only: {x,y,width,height} in CSS pixels; mutually exclusive with full_page.")),
            ["target_id"]),
        Tool(
            "snapshot",
            "Web only: capture several viewports in one call and report horizontal overflow plus offending elements. Restores the original viewport.",
            Props(
                ("target_id", "string", "Web target id."),
                ("viewports", "array", "Array of {width,height,device_scale_factor,mobile}; defaults to 390x844@2 mobile, 768x1024, 1440x900."),
                ("full_page", "boolean", "Capture full pages instead of viewports.")),
            ["target_id"]),
        Tool(
            "observe",
            "Return addressable elements: UIA tree for native windows, interactive DOM elements (ref/role/selector/bounds) for web targets.",
            Props(
                ("target_id", "string", "Target id."),
                ("max_nodes", "integer", "Maximum elements; default 100.")),
            ["target_id"]),
        Tool(
            "click",
            "Click a point from the screenshot: native HWND children use window messages; single-HWND WPF/UWP surfaces use UIA hit-testing and restore focus; web uses CDP (CSS pixels).",
            Props(
                ("target_id", "string", "Target id."),
                ("x", "integer", "Client/CSS X."),
                ("y", "integer", "Client/CSS Y."),
                ("strict_no_focus_steal", "boolean", "Refuse instead of using the UIA hit-test fallback. Default false: execute and restore focus.")),
            ["target_id", "x", "y"]),
        Tool(
            "click_element",
            "Click an element by ref or name. Native: HWND-backed controls use window messages; otherwise UIA executes and restores focus. Only a control with no actionable pattern is refused. Never moves the real cursor.",
            Props(
                ("target_id", "string", "Target id."),
                ("ref", "string", "Element ref from the latest observe, e.g. e1."),
                ("name", "string", "Accessible name for native targets; visible text for web targets."),
                ("strict_no_focus_steal", "boolean", "Refuse instead of using the UIA fallback (which briefly activates the target). Default false: execute and restore focus.")),
            ["target_id"]),
        Tool(
            "scroll",
            "Scroll at a point: native targets use wheel messages, web targets use CDP wheel events. Positive delta scrolls up.",
            Props(
                ("target_id", "string", "Target id."),
                ("x", "integer", "Client/CSS X."),
                ("y", "integer", "Client/CSS Y."),
                ("delta", "integer", "Wheel delta; 120 per notch, negative scrolls down.")),
            ["target_id", "x", "y", "delta"]),
        Tool(
            "drag",
            "Drag between two points: native window messages or CDP mouse events.",
            Props(
                ("target_id", "string", "Target id."),
                ("from_x", "integer", "Start X."),
                ("from_y", "integer", "Start Y."),
                ("to_x", "integer", "End X."),
                ("to_y", "integer", "End Y."),
                ("duration_ms", "integer", "Drag duration; default 300.")),
            ["target_id", "from_x", "from_y", "to_x", "to_y"]),
        Tool(
            "key",
            "Press a key or hotkey, e.g. [\"CTRL\",\"S\"], [\"F5\"]. Native: window messages; web: CDP key events.",
            Props(
                ("target_id", "string", "Target id."),
                ("keys", "array", "Key sequence, e.g. [\"CTRL\",\"S\"].")),
            ["target_id", "keys"]),
        Tool(
            "set_text",
            "Set text: native uses WM_SETTEXT on an EDIT/RICHEDIT child when possible; otherwise UIA ValuePattern executes and restores focus. Web uses the ref from observe.",
            Props(
                ("target_id", "string", "Target id."),
                ("automation_id", "string", "Native AutomationId, e.g. 'txtInput'."),
                ("ref", "string", "Web element ref from observe, e.g. e2."),
                ("text", "string", "New value."),
                ("strict_no_focus_steal", "boolean", "Refuse instead of using the UIA fallback (which briefly activates the target). Default false: execute and restore focus.")),
            ["target_id", "text"]),
        Tool(
            "navigate",
            "Navigate a web target to another URL and wait for load.",
            Props(
                ("target_id", "string", "Web target id."),
                ("url", "string", "Absolute http/https/file URL.")),
            ["target_id", "url"]),
        Tool(
            "viewport",
            "Change a web target's viewport (responsive checks). Width/height are CSS pixels.",
            Props(
                ("target_id", "string", "Web target id."),
                ("width", "integer", "Viewport width; default current."),
                ("height", "integer", "Viewport height; default current."),
                ("device_scale_factor", "number", "Device scale factor; default current."),
                ("mobile", "boolean", "Mobile emulation.")),
            ["target_id"]),
        Tool(
            "console",
            "Return recent console messages for a web target (errors/warnings from the page).",
            Props(
                ("target_id", "string", "Web target id."),
                ("limit", "integer", "Maximum entries; default 50.")),
            ["target_id"]),
        Tool(
            "network",
            "Return recent network failures and HTTP 4xx/5xx responses for a web target.",
            Props(
                ("target_id", "string", "Web target id."),
                ("limit", "integer", "Maximum issues; default 50.")),
            ["target_id"]),
        Tool(
            "get_html",
            "Return outerHTML for a web target (page or selector), truncated to max_chars.",
            Props(
                ("target_id", "string", "Web target id."),
                ("selector", "string", "CSS selector; omit for the whole document."),
                ("max_chars", "integer", "Truncation limit; default 200000.")),
            ["target_id"]),
        Tool(
            "close_target",
            "Restore and release one target. Web targets close their headless browser and delete the temp profile.",
            Props(("target_id", "string", "Target id.")),
            ["target_id"]),
        Tool("close_all", "Restore all targets (panic).", Props(), []),
    ];

    private static object Tool(
        string name,
        string description,
        Dictionary<string, object> properties,
        string[] required) => new
        {
            name,
            description,
            inputSchema = new
            {
                type = "object",
                properties,
                required,
                additionalProperties = false,
            },
        };

    private static Dictionary<string, object> Props(params (string Name, string Type, string Description)[] items)
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, type, description) in items)
        {
            properties[name] = new { type, description };
        }

        return properties;
    }

    private static object NativeResult(NativeActionResult result, string? space = null)
    {
        var focus = result.Focus switch
        {
            FocusRestoreStatus.NotNeeded => "not-needed",
            FocusRestoreStatus.Restored => "restored",
            _ => "stolen",
        };

        var payload = new Dictionary<string, object?>
        {
            ["ok"] = result.Ok,
            ["strategy"] = result.Strategy,
            ["focus"] = focus,
        };

        if (space is not null)
        {
            payload["space"] = space;
        }

        if (result.Focus == FocusRestoreStatus.Stolen)
        {
            payload["warning"] =
                "FOCUS_STOLEN: the action completed, but the foreground window could not be restored. Do not retry blindly.";
        }

        var text = JsonSerializer.Serialize(payload, JsonOptions);
        return TextResult(text, isError: result.Focus == FocusRestoreStatus.Stolen);
    }

    private static object TextResult(string text, bool isError = false) => new
    {
        content = new object[] { Text(text) },
        isError,
    };

    private static object ErrorResult(string code, string message) => new
    {
        content = new object[]
        {
            Text(JsonSerializer.Serialize(new { error = new { code, message } }, JsonOptions)),
        },
        isError = true,
    };

    private static object Text(string text) => new { type = "text", text };

    private static object Image(byte[] png) => new
    {
        type = "image",
        data = Convert.ToBase64String(png),
        mimeType = "image/png",
    };

    private static string? GetString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement args, string name, int fallback) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static bool GetBool(JsonElement args, string name, bool fallback) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static double GetDouble(JsonElement args, string name, double fallback) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.TryGetDouble(out var parsed)
            ? parsed
            : fallback;

    private static EdgeCdpClient.ScreenshotClip? ParseClip(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("clip", out var clip) ||
            clip.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var width = GetDouble(clip, "width", 0);
        var height = GetDouble(clip, "height", 0);
        if (width <= 0 || height <= 0)
        {
            throw new TargetException("INVALID_ARGUMENTS", "clip requires width and height > 0.");
        }

        return new EdgeCdpClient.ScreenshotClip(
            GetDouble(clip, "x", 0),
            GetDouble(clip, "y", 0),
            width,
            height);
    }

    private static IntPtr ParseHandle(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        if (long.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return new IntPtr(value);
        }

        throw new TargetException("INVALID_HWND", $"Cannot parse hwnd '{text}'.");
    }

    private async Task WriteResultAsync(JsonElement id, object result)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.ValueKind == JsonValueKind.Undefined ? null : id.Clone(),
            ["result"] = result,
        };

        await _writer.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions));
        await _writer.FlushAsync();
    }

    private async Task WriteErrorAsync(JsonElement id, int code, string message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.ValueKind == JsonValueKind.Undefined ? null : id.Clone(),
            ["error"] = new { code, message },
        };

        await _writer.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions));
        await _writer.FlushAsync();
    }
}
