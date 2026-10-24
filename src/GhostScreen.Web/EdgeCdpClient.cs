using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GhostScreen.Web;

/// <summary>Minimal Chrome DevTools Protocol client for an already-running headless Edge.</summary>
public sealed class EdgeCdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly List<ConsoleEntry> _console = new();
    private readonly List<NetworkIssue> _networkIssues = new();
    private readonly ConcurrentDictionary<string, RequestInfo> _requests = new();
    private readonly CancellationTokenSource _cts = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private Task? _receiveLoop;
    private int _nextId;
    private int _inflightRequests;
    private long _lastNetworkActivityMs;

    public sealed record ConsoleEntry(string Level, string Text);

    public sealed record NetworkIssue(
        string Kind,
        string Url,
        string Method,
        int? Status,
        string? Error,
        string ResourceType);

    private sealed record RequestInfo(string Url, string Method);

    public sealed record DomElement(
        string Tag,
        string Id,
        string Role,
        string Text,
        string Placeholder,
        double X,
        double Y,
        double Width,
        double Height,
        string Selector,
        bool Enabled);

    public sealed record PageInfo(string Title, string Url, int Width, int Height, double DevicePixelRatio);

    public bool IsConnected => _socket.State == WebSocketState.Open;

    public sealed record PageMetrics(double ContentWidth, double ContentHeight);

    public sealed record ScreenshotClip(double X, double Y, double Width, double Height);

    public sealed record OverflowElement(string Selector, double X, double Y, double Width, double Height, double Right);

    public sealed record OverflowReport(
        int InnerWidth,
        int ScrollWidth,
        bool HorizontalOverflow,
        IReadOnlyList<OverflowElement> Offenders);

    public static async Task<EdgeCdpClient> ConnectAsync(
        int port,
        string? pageUrlContains = null,
        CancellationToken cancellationToken = default)
    {
        var client = new EdgeCdpClient();
        await client.ConnectCoreAsync(port, pageUrlContains, cancellationToken).ConfigureAwait(false);
        return client;
    }

    public async Task NavigateAsync(string url, int timeoutMs = 15_000)
    {
        await SendAsync("Page.enable", null, timeoutMs).ConfigureAwait(false);
        await SendAsync("Runtime.enable", null, timeoutMs).ConfigureAwait(false);
        await SendAsync("Log.enable", null, timeoutMs).ConfigureAwait(false);
        await SendAsync("Network.enable", null, timeoutMs).ConfigureAwait(false);

        ResetNetworkActivity();
        await SendAsync("Page.navigate", new { url }, timeoutMs).ConfigureAwait(false);

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var state = await EvaluateAsync<string>("document.readyState").ConfigureAwait(false);
            if (state == "complete")
            {
                await EvaluateAsync<bool>(
                    "document.fonts ? document.fonts.ready.then(() => true) : true",
                    timeoutMs).ConfigureAwait(false);
                await WaitForNetworkIdleAsync(Math.Min(3_000, timeoutMs)).ConfigureAwait(false);
                await ResetPageScaleAsync().ConfigureAwait(false);
                await Task.Delay(150).ConfigureAwait(false);
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException("Page did not reach readyState=complete.");
    }

    /// <summary>
    /// Best-effort network idle: resolves true when no request has been in flight
    /// for <paramref name="quietMs"/>, false on timeout. Navigation does not fail
    /// when long-polling or WebSockets keep the page busy.
    /// </summary>
    public async Task<bool> WaitForNetworkIdleAsync(int timeoutMs = 3_000, int quietMs = 300)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var inflight = Math.Max(0, Volatile.Read(ref _inflightRequests));
            var idleFor = Environment.TickCount64 - Volatile.Read(ref _lastNetworkActivityMs);
            if (inflight == 0 && idleFor >= quietMs)
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return Math.Max(0, Volatile.Read(ref _inflightRequests)) == 0;
    }

    public void ResetNetworkActivity()
    {
        Interlocked.Exchange(ref _inflightRequests, 0);
        Volatile.Write(ref _lastNetworkActivityMs, Environment.TickCount64);
    }

    public async Task<T?> EvaluateAsync<T>(string expression, int timeoutMs = 10_000)
    {
        var response = await SendAsync(
            "Runtime.evaluate",
            new
            {
                expression,
                returnByValue = true,
                awaitPromise = true,
            },
            timeoutMs).ConfigureAwait(false);

        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("value", out var value))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(value.GetRawText(), JsonOptions);
    }

    public async Task CaptureScreenshotAsync(string path, int timeoutMs = 15_000)
    {
        var bytes = await CaptureScreenshotBytesAsync(timeoutMs).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
    }

    public async Task<byte[]> CaptureScreenshotBytesAsync(
        int timeoutMs = 15_000,
        bool fullPage = false,
        ScreenshotClip? clip = null)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["format"] = "png",
            ["fromSurface"] = true,
        };

        if (clip is not null)
        {
            parameters["captureBeyondViewport"] = true;
            parameters["clip"] = new
            {
                x = clip.X,
                y = clip.Y,
                width = clip.Width,
                height = clip.Height,
                scale = 1,
            };
        }
        else if (fullPage)
        {
            var metrics = await GetPageMetricsAsync(timeoutMs).ConfigureAwait(false);
            parameters["captureBeyondViewport"] = true;
            parameters["clip"] = new
            {
                x = 0,
                y = 0,
                width = metrics.ContentWidth,
                height = metrics.ContentHeight,
                scale = 1,
            };
        }
        else
        {
            parameters["captureBeyondViewport"] = false;
        }

        var response = await SendAsync("Page.captureScreenshot", parameters, timeoutMs)
            .ConfigureAwait(false);

        var data = response.GetProperty("data").GetString()
            ?? throw new InvalidOperationException("Screenshot data missing.");
        return Convert.FromBase64String(data);
    }

    public async Task<PageMetrics> GetPageMetricsAsync(int timeoutMs = 5_000)
    {
        var response = await SendAsync("Page.getLayoutMetrics", null, timeoutMs).ConfigureAwait(false);

        var size = response.TryGetProperty("cssContentSize", out var cssContentSize)
            ? cssContentSize
            : response.TryGetProperty("contentSize", out var contentSize)
                ? contentSize
                : throw new InvalidOperationException("Layout metrics missing content size.");

        return new PageMetrics(
            size.GetProperty("width").GetDouble(),
            size.GetProperty("height").GetDouble());
    }

    public async Task<OverflowReport> GetOverflowReportAsync(int timeoutMs = 5_000)
    {
        const string script = """
            (() => {
              const innerWidth = window.innerWidth;
              const doc = document.documentElement;
              const body = document.body;
              const scrollWidth = Math.max(
                doc ? doc.scrollWidth : 0,
                body ? body.scrollWidth : 0,
                innerWidth);
              const offenders = [];
              for (const el of document.querySelectorAll('body *')) {
                const r = el.getBoundingClientRect();
                if (r.width === 0 || r.height === 0) continue;
                if (r.right <= innerWidth + 1) continue;
                let selector = el.id ? '#' + CSS.escape(el.id) : el.tagName.toLowerCase();
                offenders.push({
                  selector,
                  x: r.x,
                  y: r.y,
                  width: r.width,
                  height: r.height,
                  right: r.right
                });
              }
              offenders.sort((a, b) => b.right - a.right);
              return {
                innerWidth,
                scrollWidth,
                horizontalOverflow: scrollWidth > innerWidth + 1,
                offenders: offenders.slice(0, 5)
              };
            })()
            """;

        return await EvaluateAsync<OverflowReport>(script, timeoutMs).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Overflow report unavailable.");
    }

    public async Task<PageInfo> GetPageInfoAsync(int timeoutMs = 5_000)
    {
        var info = await EvaluateAsync<PageInfo>(
            "({ title: document.title, url: location.href, width: innerWidth, height: innerHeight, devicePixelRatio: window.devicePixelRatio })",
            timeoutMs).ConfigureAwait(false);

        return info ?? throw new InvalidOperationException("Page info unavailable.");
    }

    public async Task SetViewportAsync(int width, int height, double deviceScaleFactor = 1, bool mobile = false)
    {
        await SendAsync(
            "Emulation.setDeviceMetricsOverride",
            new { width, height, deviceScaleFactor, mobile },
            10_000).ConfigureAwait(false);

        // captureBeyondViewport can leave a page scale factor behind; reset it so
        // innerWidth/innerHeight match the requested CSS viewport.
        await ResetPageScaleAsync().ConfigureAwait(false);
    }

    public async Task ResetPageScaleAsync()
    {
        try
        {
            await SendAsync(
                "Emulation.setPageScaleFactor",
                new { pageScaleFactor = 1 },
                5_000).ConfigureAwait(false);
        }
        catch
        {
            // Older builds may not support the command; metrics still apply.
        }
    }

    public async Task<(double X, double Y)> GetElementCenterAsync(string selector)
    {
        var point = await EvaluateAsync<PointDto>(
            $"(() => {{ const r = document.querySelector({JsonSerializer.Serialize(selector)}).getBoundingClientRect(); return {{ x: r.x + r.width / 2, y: r.y + r.height / 2 }}; }})()")
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Element not found.");

        return (point.X, point.Y);
    }

    public async Task ClickAsync(double x, double y)
    {
        await SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y }).ConfigureAwait(false);
        await SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }).ConfigureAwait(false);
        await SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }).ConfigureAwait(false);
    }

    public async Task ScrollAsync(double x, double y, int delta)
    {
        // Positive native deltas mean "up"; CDP's yDistance is positive up too.
        // A synthesized mouse gesture produces real wheel/scroll events in headless.
        await SendAsync(
            "Input.synthesizeScrollGesture",
            new
            {
                x,
                y,
                xDistance = 0,
                yDistance = delta,
                speed = 800,
                gestureSourceType = "mouse",
            }).ConfigureAwait(false);
    }

    public async Task DragAsync(double fromX, double fromY, double toX, double toY, int durationMs = 300)
    {
        await SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = fromX, y = fromY }).ConfigureAwait(false);
        await SendAsync(
            "Input.dispatchMouseEvent",
            new { type = "mousePressed", x = fromX, y = fromY, button = "left", buttons = 1, clickCount = 1 }).ConfigureAwait(false);

        var steps = Math.Clamp(durationMs / 25, 2, 40);
        for (var step = 1; step <= steps; step++)
        {
            var progress = (double)step / steps;
            var x = fromX + ((toX - fromX) * progress);
            var y = fromY + ((toY - fromY) * progress);
            await SendAsync(
                "Input.dispatchMouseEvent",
                new { type = "mouseMoved", x, y, button = "left", buttons = 1 }).ConfigureAwait(false);

            if (durationMs >= 50)
            {
                await Task.Delay(Math.Max(5, durationMs / steps)).ConfigureAwait(false);
            }
        }

        await SendAsync(
            "Input.dispatchMouseEvent",
            new { type = "mouseReleased", x = toX, y = toY, button = "left", buttons = 0, clickCount = 1 }).ConfigureAwait(false);
    }

    public async Task KeyAsync(string key, IReadOnlyList<string> modifiers)
    {
        var definition = CdpKeys.Resolve(key)
            ?? throw new ArgumentException($"Unsupported key '{key}'.");
        var modifierMask = CdpKeys.ModifierMask(modifiers);

        foreach (var modifier in modifiers)
        {
            if (!CdpKeys.IsModifier(modifier))
            {
                throw new ArgumentException($"'{modifier}' is not a modifier key.");
            }
        }

        var down = new Dictionary<string, object?>
        {
            ["type"] = "keyDown",
            ["key"] = definition.Key,
            ["code"] = definition.Code,
            ["windowsVirtualKeyCode"] = definition.VirtualKey,
            ["nativeVirtualKeyCode"] = definition.VirtualKey,
            ["modifiers"] = modifierMask,
        };

        var text = definition.TextFor(modifierMask);
        if (text is not null)
        {
            down["text"] = text;
            down["unmodifiedText"] = text;
        }

        await SendAsync("Input.dispatchKeyEvent", down).ConfigureAwait(false);

        await SendAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "keyUp",
                key = definition.Key,
                code = definition.Code,
                windowsVirtualKeyCode = definition.VirtualKey,
                nativeVirtualKeyCode = definition.VirtualKey,
                modifiers = modifierMask,
            }).ConfigureAwait(false);
    }

    public async Task<bool> SetTextAsync(string selector, string text)
    {
        var selectorLiteral = JsonSerializer.Serialize(selector);
        var valueLiteral = JsonSerializer.Serialize(text);
        var script = $$"""
            (() => {
              const el = document.querySelector({{selectorLiteral}});
              if (!el) return false;
              el.focus();
              if (el.isContentEditable) {
                el.textContent = {{valueLiteral}};
              } else {
                const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                if (setter) setter.call(el, {{valueLiteral}}); else el.value = {{valueLiteral}};
              }
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
              return el.isContentEditable ? el.textContent : el.value;
            })()
            """;

        var actual = await EvaluateAsync<string>(script).ConfigureAwait(false);
        return actual == text;
    }

    public async Task<string?> GetHtmlAsync(string? selector, int maxChars = 200_000)
    {
        var selectorLiteral = JsonSerializer.Serialize(selector);
        var html = await EvaluateAsync<string>(
            $"(() => {{ const el = {selectorLiteral} ? document.querySelector({selectorLiteral}) : document.documentElement; return el ? el.outerHTML : null; }})()",
            10_000).ConfigureAwait(false);

        if (html is not null && html.Length > maxChars)
        {
            return html[..maxChars];
        }

        return html;
    }

    public async Task CloseBrowserAsync()
    {
        try
        {
            await SendAsync("Browser.close", null, 3_000).ConfigureAwait(false);
        }
        catch
        {
            // The browser may close the socket before replying.
        }
    }

    public async Task InsertTextAsync(string text)
    {
        await SendAsync("Input.insertText", new { text }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DomElement>> GetInteractiveElementsAsync()
    {
        const string script = """
            (() => {
              const cssPath = (el) => {
                if (el.id) return '#' + CSS.escape(el.id);
                const parts = [];
                while (el && el.nodeType === 1 && el !== document.body) {
                  let part = el.tagName.toLowerCase();
                  if (el.name) part += '[name="' + String(el.name).replace(/"/g, '\\"') + '"]';
                  const parent = el.parentElement;
                  if (parent) {
                    const siblings = [...parent.children].filter(c => c.tagName === el.tagName);
                    if (siblings.length > 1) part += ':nth-of-type(' + (siblings.indexOf(el) + 1) + ')';
                  }
                  parts.unshift(part);
                  el = parent;
                }
                return 'body > ' + parts.join(' > ');
              };

              const roleOf = (el) => {
                const explicit = el.getAttribute('role');
                if (explicit) return explicit;
                const tag = el.tagName.toLowerCase();
                if (tag === 'a') return 'link';
                if (tag === 'button') return 'button';
                if (tag === 'select') return 'combobox';
                if (tag === 'textarea') return 'textbox';
                if (tag === 'input') {
                  const type = (el.type || 'text').toLowerCase();
                  if (type === 'checkbox' || type === 'radio') return type;
                  if (type === 'submit' || type === 'button') return 'button';
                  return 'textbox';
                }
                return tag;
              };

              const items = [];
              const nodes = document.querySelectorAll(
                'button, a[href], input, textarea, select, [role="button"], [contenteditable="true"], [onclick]');
              for (const el of nodes) {
                const r = el.getBoundingClientRect();
                if (r.width === 0 || r.height === 0) continue;
                const label = (el.getAttribute('aria-label') || el.innerText || el.value || el.placeholder || el.name || el.title || '').trim();
                items.push({
                  tag: el.tagName.toLowerCase(),
                  id: el.id || '',
                  role: roleOf(el),
                  text: label.slice(0, 120),
                  placeholder: el.placeholder || '',
                  x: r.x, y: r.y, width: r.width, height: r.height,
                  selector: cssPath(el),
                  enabled: !el.disabled
                });
              }
              return items;
            })()
            """;

        return await EvaluateAsync<List<DomElement>>(script).ConfigureAwait(false)
            ?? [];
    }

    public IReadOnlyList<ConsoleEntry> Console
    {
        get
        {
            lock (_console)
            {
                return _console.ToArray();
            }
        }
    }

    public IReadOnlyList<NetworkIssue> NetworkIssues
    {
        get
        {
            lock (_networkIssues)
            {
                return _networkIssues.ToArray();
            }
        }
    }

    private void RecordNetworkIssue(NetworkIssue issue)
    {
        lock (_networkIssues)
        {
            _networkIssues.Add(issue);
            if (_networkIssues.Count > 200)
            {
                _networkIssues.RemoveRange(0, _networkIssues.Count - 200);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort.
        }

        _cts.Cancel();
        _socket.Dispose();

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private async Task ConnectCoreAsync(
        int port,
        string? pageUrlContains,
        CancellationToken cancellationToken)
    {
        var pageSocketUrl = await WaitForPageTargetAsync(port, pageUrlContains, cancellationToken)
            .ConfigureAwait(false);
        await _socket.ConnectAsync(new Uri(pageSocketUrl), cancellationToken).ConfigureAwait(false);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
    }

    private static async Task<string> WaitForPageTargetAsync(
        int port,
        string? pageUrlContains,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = Environment.TickCount64 + 15_000;

        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken)
                    .ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                foreach (var target in document.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out var type) &&
                        type.GetString() == "page" &&
                        target.TryGetProperty("webSocketDebuggerUrl", out var socketUrl))
                    {
                        var pageUrl = target.TryGetProperty("url", out var urlElement)
                            ? urlElement.GetString()
                            : null;

                        if (pageUrlContains is not null &&
                            (pageUrl is null ||
                             !pageUrl.Contains(pageUrlContains, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        var url = socketUrl.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            return url;
                        }
                    }
                }
            }
            catch
            {
                // Edge is still starting.
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Edge DevTools endpoint did not become ready.");
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        var builder = new StringBuilder();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var message = builder.ToString();
                builder.Clear();
                HandleMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (WebSocketException)
        {
            // Normal shutdown / browser exit.
        }
    }

    private void HandleMessage(string message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;

        if (root.TryGetProperty("id", out var idElement) &&
            idElement.TryGetInt32(out var id) &&
            _pending.TryRemove(id, out var completion))
        {
            if (root.TryGetProperty("error", out var error))
            {
                completion.TrySetException(new InvalidOperationException(error.ToString()));
            }
            else if (root.TryGetProperty("result", out var result))
            {
                completion.TrySetResult(result.Clone());
            }
            else
            {
                completion.TrySetResult(default);
            }

            return;
        }

        if (!root.TryGetProperty("method", out var methodElement))
        {
            return;
        }

        var method = methodElement.GetString();

        if (method == "Network.requestWillBeSent" &&
            root.TryGetProperty("params", out var requestParams) &&
            requestParams.TryGetProperty("requestId", out var requestIdElement) &&
            requestParams.TryGetProperty("request", out var request))
        {
            var requestId = requestIdElement.GetString() ?? string.Empty;
            var url = request.TryGetProperty("url", out var urlElement)
                ? urlElement.GetString() ?? string.Empty
                : string.Empty;
            var requestMethod = request.TryGetProperty("method", out var requestMethodElement)
                ? requestMethodElement.GetString() ?? "GET"
                : "GET";

            if (requestId.Length > 0)
            {
                _requests[requestId] = new RequestInfo(url, requestMethod);
            }

            Interlocked.Increment(ref _inflightRequests);
            Volatile.Write(ref _lastNetworkActivityMs, Environment.TickCount64);
            return;
        }

        if (method == "Network.responseReceived" &&
            root.TryGetProperty("params", out var responseParams) &&
            responseParams.TryGetProperty("response", out var response) &&
            response.TryGetProperty("status", out var statusElement) &&
            statusElement.TryGetInt32(out var status) &&
            status >= 400)
        {
            var requestId = responseParams.TryGetProperty("requestId", out var responseRequestId)
                ? responseRequestId.GetString() ?? string.Empty
                : string.Empty;
            _requests.TryGetValue(requestId, out var requestInfo);
            var url = response.TryGetProperty("url", out var responseUrl)
                ? responseUrl.GetString() ?? string.Empty
                : requestInfo?.Url ?? string.Empty;
            var type = responseParams.TryGetProperty("type", out var responseType)
                ? responseType.GetString() ?? string.Empty
                : string.Empty;

            RecordNetworkIssue(new NetworkIssue(
                "http_error",
                url,
                requestInfo?.Method ?? string.Empty,
                status,
                null,
                type));
            return;
        }

        if (method is "Network.loadingFinished" or "Network.loadingFailed")
        {
            Interlocked.Decrement(ref _inflightRequests);
            Volatile.Write(ref _lastNetworkActivityMs, Environment.TickCount64);

            if (root.TryGetProperty("params", out var loadingParams) &&
                loadingParams.TryGetProperty("requestId", out var loadingRequestId))
            {
                var requestId = loadingRequestId.GetString() ?? string.Empty;
                _requests.TryRemove(requestId, out var requestInfo);

                if (method == "Network.loadingFailed")
                {
                    var error = loadingParams.TryGetProperty("errorText", out var errorElement)
                        ? errorElement.GetString()
                        : null;
                    var type = loadingParams.TryGetProperty("type", out var loadingType)
                        ? loadingType.GetString() ?? string.Empty
                        : string.Empty;

                    RecordNetworkIssue(new NetworkIssue(
                        "failed",
                        requestInfo?.Url ?? string.Empty,
                        requestInfo?.Method ?? string.Empty,
                        null,
                        error,
                        type));
                }
            }

            return;
        }

        if (method == "Runtime.consoleAPICalled" &&
            root.TryGetProperty("params", out var parameters) &&
            parameters.TryGetProperty("args", out var args))
        {
            var text = string.Join(
                " ",
                args.EnumerateArray().Select(argument =>
                    argument.TryGetProperty("value", out var value)
                        ? value.ToString()
                        : argument.TryGetProperty("description", out var description)
                            ? description.GetString()
                            : string.Empty));

            var level = parameters.TryGetProperty("type", out var type)
                ? type.GetString() ?? "log"
                : "log";

            lock (_console)
            {
                _console.Add(new ConsoleEntry(level, text));
                if (_console.Count > 500)
                {
                    _console.RemoveRange(0, _console.Count - 500);
                }
            }
        }
    }

    private async Task<JsonElement> SendAsync(string method, object? parameters, int timeoutMs = 10_000)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
        };

        if (parameters is not null)
        {
            payload["params"] = parameters;
        }

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);

        await _socket.SendAsync(
            new ArraySegment<byte>(payloadBytes),
            WebSocketMessageType.Text,
            true,
            _cts.Token).ConfigureAwait(false);

        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private sealed record PointDto(double X, double Y);
}
