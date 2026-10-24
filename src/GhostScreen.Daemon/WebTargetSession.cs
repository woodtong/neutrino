using System.Security.Cryptography;
using GhostScreen.Core;
using GhostScreen.Web;

namespace GhostScreen.Daemon;

public sealed record WebElement(
    string Ref,
    string Tag,
    string Role,
    string Name,
    string Selector,
    Rect Bounds,
    bool Enabled);

public sealed record WebObservation(int Epoch, IReadOnlyList<WebElement> Elements);

public sealed record WebCapture(
    byte[] Png,
    int Width,
    int Height,
    string Hash,
    long CapturedAtUnixMs,
    string Url,
    string Title,
    double DeviceScaleFactor,
    int CssWidth,
    int CssHeight,
    string Region,
    EdgeCdpClient.ScreenshotClip? Clip);

public sealed record ViewportSpec(int Width, int Height, double DeviceScaleFactor, bool Mobile);

public sealed record SnapshotShot(
    int Index,
    ViewportSpec Viewport,
    WebCapture Capture,
    EdgeCdpClient.OverflowReport Overflow);

/// <summary>A headless Edge page owned by one agent session. Operations are serialized.</summary>
public sealed class WebTargetSession : ITarget
{
    private readonly object _gate = new();
    private readonly EdgeBrowser? _browser;
    private readonly EdgeCdpClient _client;
    private IReadOnlyList<WebElement> _lastObservation = [];
    private int _epoch;
    private bool _closed;
    private int _requestedWidth;
    private int _requestedHeight;

    internal WebTargetSession(
        string id,
        EdgeBrowser browser,
        EdgeCdpClient client,
        WebLaunchOptions options,
        string url,
        string title,
        int cssWidth,
        int cssHeight)
    {
        Id = id;
        _browser = browser;
        _client = client;
        Url = url;
        Title = title;
        CssWidth = cssWidth;
        CssHeight = cssHeight;
        _requestedWidth = options.Width;
        _requestedHeight = options.Height;
        DeviceScaleFactor = options.DeviceScaleFactor;
        Mobile = options.Mobile;
        CreatedAt = DateTimeOffset.UtcNow;
        LastUsed = CreatedAt;
    }

    /// <summary>Attach to an already-running browser; closing detaches only.</summary>
    internal WebTargetSession(
        string id,
        EdgeCdpClient client,
        string url,
        string title,
        int cssWidth,
        int cssHeight,
        double deviceScaleFactor)
    {
        Id = id;
        _browser = null;
        _client = client;
        Url = url;
        Title = title;
        CssWidth = cssWidth;
        CssHeight = cssHeight;
        _requestedWidth = cssWidth;
        _requestedHeight = cssHeight;
        DeviceScaleFactor = deviceScaleFactor;
        Mobile = false;
        CreatedAt = DateTimeOffset.UtcNow;
        LastUsed = CreatedAt;
    }

    public string Id { get; }

    public string Kind => "web";

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastUsed { get; private set; }

    public string Url { get; private set; }

    public string Title { get; private set; }

    public int CssWidth { get; private set; }

    public int CssHeight { get; private set; }

    public double DeviceScaleFactor { get; private set; }

    public bool Mobile { get; private set; }

    public int ProcessId => _browser?.ProcessId ?? 0;

    public string ProfileDirectory => _browser?.ProfileDirectory ?? string.Empty;

    public DateTime ProcessStartTime => _browser?.Process.StartTime ?? default;

    public bool OwnsBrowser => _browser is not null;

    public async Task<WebCapture> CaptureAsync(
        bool fullPage = false,
        EdgeCdpClient.ScreenshotClip? clip = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();

        if (fullPage && clip is not null)
        {
            throw new TargetException("INVALID_ARGUMENTS", "full_page and clip are mutually exclusive.");
        }

        if (clip is not null)
        {
            if (clip.X < 0 || clip.Y < 0 || clip.Width <= 0 || clip.Height <= 0)
            {
                throw new TargetException(
                    "INVALID_ARGUMENTS",
                    "clip requires x/y >= 0 and width/height > 0 (CSS pixels).");
            }

            if (clip.Width > 16_384 || clip.Height > 16_384)
            {
                throw new TargetException("CAPTURE_TOO_LARGE", "clip must not exceed 16384x16384 CSS pixels.");
            }
        }

        if (fullPage)
        {
            var metrics = await _client.GetPageMetricsAsync().ConfigureAwait(false);
            if (metrics.ContentWidth > 16_384 || metrics.ContentHeight > 16_384)
            {
                throw new TargetException(
                    "CAPTURE_TOO_LARGE",
                    $"Page content is {metrics.ContentWidth}x{metrics.ContentHeight}; use clip for oversized pages.");
            }
        }

        var png = await _client.CaptureScreenshotBytesAsync(15_000, fullPage, clip)
            .ConfigureAwait(false);

        // captureBeyondViewport may resize the layout viewport; restore the
        // caller's viewport before reporting metrics or serving the next action.
        if (fullPage || clip is not null)
        {
            await _client.SetViewportAsync(_requestedWidth, _requestedHeight, DeviceScaleFactor, Mobile)
                .ConfigureAwait(false);
        }

        var page = await _client.GetPageInfoAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var size = PngInfo.TryReadSize(png)
            ?? throw new TargetException("CAPTURE_FAILED", "CDP returned an invalid PNG.");
        var hash = Convert.ToHexString(SHA256.HashData(png))[..16].ToLowerInvariant();
        var region = clip is not null ? "clip" : fullPage ? "full_page" : "viewport";

        lock (_gate)
        {
            RefreshPage(page);
            Touch();
        }

        return new WebCapture(
            png,
            size.Width,
            size.Height,
            hash,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            page.Url,
            page.Title,
            page.DevicePixelRatio,
            page.Width,
            page.Height,
            region,
            clip);
    }

    public async Task<IReadOnlyList<SnapshotShot>> SnapshotAsync(
        IReadOnlyList<ViewportSpec> viewports,
        bool fullPage,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        if (viewports.Count == 0)
        {
            throw new TargetException("INVALID_ARGUMENTS", "snapshot requires at least one viewport.");
        }

        var original = new ViewportSpec(_requestedWidth, _requestedHeight, DeviceScaleFactor, Mobile);
        var shots = new List<SnapshotShot>(viewports.Count);

        try
        {
            for (var index = 0; index < viewports.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var viewport = viewports[index];
                await _client.SetViewportAsync(
                    viewport.Width,
                    viewport.Height,
                    viewport.DeviceScaleFactor,
                    viewport.Mobile).ConfigureAwait(false);
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);

                var capture = await CaptureAsync(fullPage, null, cancellationToken).ConfigureAwait(false);
                var overflow = await _client.GetOverflowReportAsync().ConfigureAwait(false);

                lock (_gate)
                {
                    _requestedWidth = viewport.Width;
                    _requestedHeight = viewport.Height;
                    DeviceScaleFactor = viewport.DeviceScaleFactor;
                    Mobile = viewport.Mobile;
                }

                shots.Add(new SnapshotShot(index, viewport, capture, overflow));
            }
        }
        finally
        {
            try
            {
                await _client.SetViewportAsync(
                    original.Width,
                    original.Height,
                    original.DeviceScaleFactor,
                    original.Mobile).ConfigureAwait(false);
                var page = await _client.GetPageInfoAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _requestedWidth = original.Width;
                    _requestedHeight = original.Height;
                    DeviceScaleFactor = original.DeviceScaleFactor;
                    Mobile = original.Mobile;
                    RefreshPage(page);
                    Touch();
                }
            }
            catch
            {
                // The target may already be closing; the caller still gets the shots.
            }
        }

        return shots;
    }

    public async Task<WebObservation> ObserveAsync(int maxNodes = 100)
    {
        EnsureOpen();
        var elements = await _client.GetInteractiveElementsAsync().ConfigureAwait(false);

        lock (_gate)
        {
            EnsureOpen();
            _epoch++;
            _lastObservation = elements
                .Take(Math.Clamp(maxNodes, 1, 1_000))
                .Select((element, index) => new WebElement(
                    $"e{index + 1}",
                    element.Tag,
                    element.Role,
                    element.Text,
                    element.Selector,
                    new Rect(
                        (int)Math.Round(element.X),
                        (int)Math.Round(element.Y),
                        (int)Math.Round(element.Width),
                        (int)Math.Round(element.Height)),
                    element.Enabled))
                .ToArray();
            Touch();
            return new WebObservation(_epoch, _lastObservation);
        }
    }

    public async Task<bool> ClickPointAsync(int x, int y)
    {
        EnsureOpen();
        ValidatePoint(x, y);
        await _client.ClickAsync(x, y).ConfigureAwait(false);
        Touch();
        return true;
    }

    public async Task<bool> ClickRefAsync(string reference)
    {
        EnsureOpen();
        WebElement? element;
        lock (_gate)
        {
            element = _lastObservation.FirstOrDefault(candidate =>
                candidate.Ref.Equals(reference, StringComparison.OrdinalIgnoreCase));
        }

        if (element is null)
        {
            throw new TargetException(
                "ELEMENT_STALE",
                $"Ref '{reference}' is not in the latest observation; call observe again.");
        }

        var center = await _client.GetElementCenterAsync(element.Selector).ConfigureAwait(false);
        await _client.ClickAsync(center.X, center.Y).ConfigureAwait(false);
        Touch();
        return true;
    }

    public async Task<bool> ClickNameAsync(string name)
    {
        EnsureOpen();
        var elements = await _client.GetInteractiveElementsAsync().ConfigureAwait(false);
        var match = elements.FirstOrDefault(element =>
                element.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? elements.FirstOrDefault(element =>
                element.Text.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new TargetException("ELEMENT_NOT_FOUND", $"No interactive element matches '{name}'.");
        }

        var center = await _client.GetElementCenterAsync(match.Selector).ConfigureAwait(false);
        await _client.ClickAsync(center.X, center.Y).ConfigureAwait(false);
        Touch();
        return true;
    }

    public async Task<bool> ScrollAsync(int x, int y, int delta)
    {
        EnsureOpen();
        ValidatePoint(x, y);
        await _client.ScrollAsync(x, y, delta).ConfigureAwait(false);
        Touch();
        return true;
    }

    public async Task<bool> DragAsync(int startX, int startY, int endX, int endY, int durationMs)
    {
        EnsureOpen();
        ValidatePoint(startX, startY);
        ValidatePoint(endX, endY);
        await _client.DragAsync(startX, startY, endX, endY, durationMs).ConfigureAwait(false);
        Touch();
        return true;
    }

    public async Task<bool> PressKeyAsync(IReadOnlyList<string> keys)
    {
        EnsureOpen();
        if (keys.Count == 0)
        {
            throw new TargetException("INVALID_ARGUMENTS", "key requires at least one key.");
        }

        var main = keys[^1];
        var modifiers = keys.Take(keys.Count - 1).ToArray();

        try
        {
            await _client.KeyAsync(main, modifiers).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new TargetException("INVALID_ARGUMENTS", ex.Message);
        }

        Touch();
        return true;
    }

    public async Task<bool> SetTextByRefAsync(string reference, string text)
    {
        EnsureOpen();
        WebElement? element;
        lock (_gate)
        {
            element = _lastObservation.FirstOrDefault(candidate =>
                candidate.Ref.Equals(reference, StringComparison.OrdinalIgnoreCase));
        }

        if (element is null)
        {
            throw new TargetException(
                "ELEMENT_STALE",
                $"Ref '{reference}' is not in the latest observation; call observe again.");
        }

        var ok = await _client.SetTextAsync(element.Selector, text).ConfigureAwait(false);
        Touch();
        return ok;
    }

    public async Task SetViewportAsync(int width, int height, double deviceScaleFactor, bool mobile)
    {
        EnsureOpen();
        await _client.SetViewportAsync(width, height, deviceScaleFactor, mobile).ConfigureAwait(false);
        var page = await _client.GetPageInfoAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _requestedWidth = width;
            _requestedHeight = height;
            DeviceScaleFactor = deviceScaleFactor;
            Mobile = mobile;
            RefreshPage(page);
            Touch();
        }
    }

    public async Task NavigateAsync(string url)
    {
        EnsureOpen();
        var normalized = EdgeBrowser.NormalizeUrl(url);

        try
        {
            await _client.NavigateAsync(normalized).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TargetException("NAVIGATION_TIMEOUT", ex.Message);
        }

        // A previous mobile shrink-to-fit can survive navigation; re-applying
        // the requested viewport makes the new document use it from the start.
        await _client.SetViewportAsync(_requestedWidth, _requestedHeight, DeviceScaleFactor, Mobile)
            .ConfigureAwait(false);

        var page = await _client.GetPageInfoAsync().ConfigureAwait(false);
        lock (_gate)
        {
            RefreshPage(page);
            Touch();
        }
    }

    public Task<string?> GetHtmlAsync(string? selector, int maxChars) =>
        _client.GetHtmlAsync(selector, Math.Clamp(maxChars, 1_000, 1_000_000));

    public IReadOnlyList<EdgeCdpClient.ConsoleEntry> ConsoleEntries => _client.Console;

    public IReadOnlyList<EdgeCdpClient.NetworkIssue> NetworkIssues => _client.NetworkIssues;

    public object ToDto() => new
    {
        id = Id,
        kind = Kind,
        state = _closed ? "closed" : "ready",
        title = Title,
        url = Url,
        viewport = new
        {
            width = CssWidth,
            height = CssHeight,
            device_scale_factor = DeviceScaleFactor,
            mobile = Mobile,
        },
        process = _browser is null ? null : new { pid = _browser.ProcessId, name = "msedge" },
        external = _browser is null,
        capabilities = new
        {
            capture = new[] { "cdp" },
            observe = new[] { "dom" },
            input = new[] { "cdp" },
            web = true,
        },
        createdAt = CreatedAt,
        lastUsed = LastUsed,
    };

    public void Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
        }

        if (_browser is not null)
        {
            try
            {
                _client.CloseBrowserAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Best effort.
            }
        }

        try
        {
            _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Best effort.
        }

        if (_browser is not null)
        {
            try
            {
                _browser.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Best effort.
            }
        }
    }

    public void Dispose() => Close();

    private void EnsureOpen()
    {
        if (_closed)
        {
            throw new TargetException("TARGET_CLOSED", $"Target {Id} is closed.");
        }

        if (!_client.IsConnected)
        {
            throw new TargetException(
                "TARGET_CLOSED",
                $"Browser connection for target {Id} is closed; close it and open/attach again.");
        }
    }

    private void ValidatePoint(int x, int y)
    {
        if (x < 0 || y < 0 || x >= CssWidth || y >= CssHeight)
        {
            throw new TargetException(
                "INVALID_ARGUMENTS",
                $"Point ({x},{y}) is outside the {CssWidth}x{CssHeight} viewport (CSS pixels).");
        }
    }

    private void RefreshPage(EdgeCdpClient.PageInfo page)
    {
        Title = page.Title;
        Url = page.Url;
        CssWidth = page.Width;
        CssHeight = page.Height;
    }

    private void Touch() => LastUsed = DateTimeOffset.UtcNow;
}
