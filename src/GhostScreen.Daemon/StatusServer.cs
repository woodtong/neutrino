using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostScreen.Capture;
using GhostScreen.Core;

namespace GhostScreen.Daemon;

/// <summary>
/// Minimal loopback HTTP endpoint for a human to see targets and panic.
/// Uses a raw TcpListener (no Http.sys URL ACL, no admin) and requires a
/// per-process token on every request, including the page itself.
/// </summary>
public sealed class StatusServer : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly TargetManager _manager;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(8);
    private readonly string _endpointFile;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private Task? _acceptLoop;
    private bool _disposed;

    public StatusServer(
        TargetManager manager,
        int port = 0,
        string? token = null,
        string? endpointFile = null)
    {
        _manager = manager;
        Token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _endpointFile = endpointFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostScreen",
            "status.json");
    }

    public string Token { get; }

    public int Port { get; private set; }

    public string Url => $"http://127.0.0.1:{Port}/?token={Token}";

    public void Start()
    {
        if (_acceptLoop is not null)
        {
            return;
        }

        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        WriteEndpointFile();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _listener.Stop();

        try
        {
            if (File.Exists(_endpointFile))
            {
                File.Delete(_endpointFile);
            }
        }
        catch
        {
            // Best effort.
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            if (!await _slots.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                client.ReceiveTimeout = 5_000;
                client.SendTimeout = 5_000;
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                await RouteAsync(stream, request).ConfigureAwait(false);
            }
            catch
            {
                // A status client disconnecting is not an error.
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    private async Task RouteAsync(NetworkStream stream, Request request)
    {
        if (!IsAuthorized(request))
        {
            await WriteAsync(stream, 401, "application/json", "{\"error\":\"unauthorized\"}")
                .ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && request.Path == "/")
        {
            await WriteAsync(stream, 200, "text/html; charset=utf-8", BuildPage())
                .ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && request.Path == "/api/status")
        {
            await WriteAsync(stream, 200, "application/json", BuildStatusJson())
                .ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && request.Path == "/api/screenshot")
        {
            await ServeScreenshotAsync(stream, request).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && request.Path == "/api/close_target")
        {
            var targetId = request.Query.TryGetValue("target_id", out var closeTargetId)
                ? closeTargetId
                : null;

            if (string.IsNullOrWhiteSpace(targetId))
            {
                await WriteAsync(stream, 400, "application/json", "{\"error\":\"target_id required\"}")
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                var closed = _manager.Close(targetId);
                await WriteAsync(
                    stream,
                    closed ? 200 : 404,
                    "application/json",
                    JsonSerializer.Serialize(new { ok = closed, target_id = targetId }, JsonOptions))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(stream, 500, "CLOSE_FAILED", ex.Message).ConfigureAwait(false);
            }

            return;
        }

        if (request.Method == "POST" && request.Path == "/api/panic")
        {
            var closed = _manager.List().Count;
            _manager.CloseAll();
            await WriteAsync(
                stream,
                200,
                "application/json",
                JsonSerializer.Serialize(new { ok = true, closed }, JsonOptions)).ConfigureAwait(false);
            return;
        }

        await WriteAsync(stream, 404, "application/json", "{\"error\":\"not found\"}")
            .ConfigureAwait(false);
    }

    private string BuildStatusJson()
    {
        var recovery = _manager.LastRecovery;
        var payload = new
        {
            version = ProductInfo.Version,
            pid = Environment.ProcessId,
            uptime_seconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            recovery = new
            {
                restored = recovery.RestoredApps,
                skipped = recovery.SkippedApps,
                killed_browsers = recovery.KilledBrowsers,
                deleted_profiles = recovery.DeletedProfiles,
                swept_profiles = recovery.SweptProfiles,
            },
            targets = _manager.List().Select(target => target.ToDto()).ToArray(),
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private bool IsAuthorized(Request request)
    {
        var candidate = request.Query.TryGetValue("token", out var queryToken)
            ? queryToken
            : request.Headers.TryGetValue("X-GhostScreen-Token", out var headerToken)
                ? headerToken
                : null;

        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var provided = Encoding.UTF8.GetBytes(candidate);
        var expected = Encoding.UTF8.GetBytes(Token);
        return provided.Length == expected.Length &&
               CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private async Task ServeScreenshotAsync(NetworkStream stream, Request request)
    {
        var targetId = request.Query.TryGetValue("target_id", out var value) ? value : null;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            await WriteAsync(stream, 400, "application/json", "{\"error\":\"target_id required\"}")
                .ConfigureAwait(false);
            return;
        }

        ITarget target;
        try
        {
            target = _manager.Get(targetId);
        }
        catch (TargetException)
        {
            await WriteAsync(stream, 404, "application/json", "{\"error\":\"target not found\"}")
                .ConfigureAwait(false);
            return;
        }

        try
        {
            byte[] png;

            if (target is WebTargetSession web)
            {
                var capture = await web.CaptureAsync(cancellationToken: _cts.Token)
                    .WaitAsync(TimeSpan.FromSeconds(6), _cts.Token)
                    .ConfigureAwait(false);
                png = capture.Png;
            }
            else if (target is TargetSession native)
            {
                var result = await Task
                    .Run(() => native.Capture(new CaptureOptions(2_000, 0)), _cts.Token)
                    .WaitAsync(TimeSpan.FromSeconds(4), _cts.Token)
                    .ConfigureAwait(false);
                png = PngWriter.EncodeBgraAsPng(
                    result.Frame.Bgra,
                    result.Frame.Width,
                    result.Frame.Height);
            }
            else
            {
                await WriteAsync(stream, 404, "application/json", "{\"error\":\"unsupported target\"}")
                    .ConfigureAwait(false);
                return;
            }

            await WriteBytesAsync(stream, 200, "image/png", png).ConfigureAwait(false);
        }
        catch (TargetException ex)
        {
            var status = ex.Code switch
            {
                "TARGET_HUNG" => 409,
                "TARGET_CLOSED" => 410,
                _ => 500,
            };
            await WriteErrorAsync(stream, status, ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (CaptureException ex)
        {
            var status = ex.Code == "CAPTURE_TIMEOUT" ? 504 : 500;
            await WriteErrorAsync(stream, status, ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            await WriteErrorAsync(stream, 504, "TIMEOUT", ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Server is shutting down.
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(stream, 500, "INTERNAL", ex.Message).ConfigureAwait(false);
        }
    }

    private static Task WriteErrorAsync(NetworkStream stream, int status, string code, string message) =>
        WriteAsync(
            stream,
            status,
            "application/json",
            JsonSerializer.Serialize(new { error = code, message }));

    private void WriteEndpointFile()
    {
        try
        {
            var directory = Path.GetDirectoryName(_endpointFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                new { url = Url, token = Token, port = Port, pid = Environment.ProcessId },
                JsonOptions);
            File.WriteAllText(_endpointFile, json);
        }
        catch
        {
            // A read-only profile should not stop the MCP server.
        }
    }

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[4 * 1024];
        var builder = new StringBuilder();

        while (builder.Length < MaxRequestBytes)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var text = builder.ToString();
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
        {
            return null;
        }

        var lines = text[..headerEnd].Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return null;
        }

        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var target = parts[1];
        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target[..queryIndex] : target;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (queryIndex >= 0 && queryIndex + 1 < target.Length)
        {
            foreach (var pair in target[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                var key = equals >= 0 ? pair[..equals] : pair;
                var value = equals >= 0 ? pair[(equals + 1)..] : string.Empty;
                query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
            }
        }

        return new Request(parts[0].ToUpperInvariant(), path, headers, query);
    }

    private static Task WriteAsync(
        NetworkStream stream,
        int status,
        string contentType,
        string body) =>
        WriteBytesAsync(stream, status, contentType, Encoding.UTF8.GetBytes(body));

    private static async Task WriteBytesAsync(
        NetworkStream stream,
        int status,
        string contentType,
        byte[] body)
    {
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ')
            .Append(ReasonPhrase(status))
            .Append("\r\nContent-Type: ").Append(contentType)
            .Append("\r\nContent-Length: ").Append(body.Length)
            .Append("\r\nCache-Control: no-store")
            .Append("\r\nConnection: close\r\n\r\n")
            .ToString();

        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        409 => "Conflict",
        410 => "Gone",
        500 => "Internal Server Error",
        504 => "Gateway Timeout",
        _ => "Error",
    };

    private string BuildPage()
    {
        var page = """
<!doctype html>
<html lang="zh">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>GhostScreen 状态</title>
<style>
  :root { color-scheme: dark; }
  body { margin: 0; font: 14px/1.5 system-ui, sans-serif; background: #11151c; color: #e6edf3; }
  header { padding: 16px 20px; border-bottom: 1px solid #263041; display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
  h1 { font-size: 16px; margin: 0; }
  .muted { color: #8b98a9; }
  .pill { background: #1b2430; border: 1px solid #2c3a4d; border-radius: 999px; padding: 2px 8px; font-size: 12px; }
  main { padding: 20px; display: grid; gap: 12px; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); }
  .card { background: #161d27; border: 1px solid #263041; border-radius: 10px; padding: 14px; }
  .card h2 { font-size: 14px; margin: 0 0 6px; overflow-wrap: anywhere; }
  .shot { width: 100%; height: auto; margin-top: 10px; border-radius: 6px; background: #0b0f14; display: block; }
  .shot.failed { display: none; }
  .row { display: flex; justify-content: space-between; gap: 8px; }
  .kind { color: #7ee787; text-transform: uppercase; font-size: 11px; letter-spacing: .08em; }
  .state-ready { color: #7ee787; } .state-degraded { color: #f2cc60; } .state-closed { color: #8b98a9; }
  button { background: #b62324; color: #fff; border: 0; border-radius: 8px; padding: 8px 14px; cursor: pointer; }
  button:hover { background: #d12426; }
  #error { color: #ff7b72; min-height: 1.5em; }
</style>
</head>
<body>
<header>
  <h1>GhostScreen 状态</h1>
  <span class="pill" id="version">—</span>
  <span class="pill" id="pid">pid —</span>
  <span class="muted" id="updated">等待刷新…</span>
  <span style="flex:1"></span>
  <button id="panic">急停（关闭所有 target）</button>
</header>
<main id="cards"></main>
<div id="error" style="padding: 0 20px 20px"></div>
<script>
  const token = new URLSearchParams(location.search).get('token') || '';
  const api = (path) => path + '?token=' + encodeURIComponent(token);
  const cards = document.getElementById('cards');
  const error = document.getElementById('error');
  const shots = new Map();

  function shotFor(t) {
    let img = shots.get(t.id);
    if (!img) {
      img = document.createElement('img');
      img.className = 'shot';
      img.alt = '最近截图';
      img.src = api('/api/screenshot') + '&target_id=' + encodeURIComponent(t.id) + '&stamp=' + Date.now();
      img.addEventListener('error', () => img.classList.add('failed'));
      img.addEventListener('load', () => img.classList.remove('failed'));
      shots.set(t.id, img);
    }
    return img;
  }

  function render(status) {
    document.getElementById('version').textContent = 'v' + status.version;
    document.getElementById('pid').textContent = 'pid ' + status.pid;
    document.getElementById('updated').textContent = '运行 ' + status.uptime_seconds + 's · ' + status.targets.length + ' 个 target';
    cards.replaceChildren();
    for (const t of status.targets) {
      const card = document.createElement('section');
      card.className = 'card';
      const h = document.createElement('h2');
      h.textContent = t.title || t.id;
      const meta = document.createElement('div');
      meta.className = 'row';
      const kind = document.createElement('span');
      kind.className = 'kind';
      kind.textContent = t.kind;
      const state = document.createElement('span');
      state.className = 'state-' + (t.state || 'ready');
      state.textContent = t.state + (t.degraded_reason ? ' · ' + t.degraded_reason : '');
      meta.append(kind, state);
      const id = document.createElement('div');
      id.className = 'muted';
      id.textContent = t.id + (t.url ? ' · ' + t.url : '');
      card.append(h, meta, id);
      card.append(shotFor(t));
      cards.append(card);
    }
  }

  async function refresh() {
    try {
      const response = await fetch(api('/api/status'), { cache: 'no-store' });
      if (!response.ok) throw new Error('HTTP ' + response.status);
      render(await response.json());
      error.textContent = '';
    } catch (e) {
      error.textContent = '状态读取失败：' + e.message;
    }
  }

  document.getElementById('panic').addEventListener('click', async () => {
    if (!confirm('关闭所有 target 并还原窗口？')) return;
    try {
      const response = await fetch(api('/api/panic'), { method: 'POST' });
      if (!response.ok) throw new Error('HTTP ' + response.status);
      const result = await response.json();
      error.textContent = '已关闭 ' + result.closed + ' 个 target';
      await refresh();
    } catch (e) {
      error.textContent = '急停失败：' + e.message;
    }
  });

  refresh();
  setInterval(refresh, 1000);

  // Thumbnails refresh at 0.2fps: a native PrintWindow capture forces a paint,
  // so a 1s/2-5fps loop would be wasteful for a passive status page.
  setInterval(() => {
    for (const [id, img] of shots) {
      if (!img.isConnected) {
        shots.delete(id);
        continue;
      }
      img.src = api('/api/screenshot') + '&target_id=' + encodeURIComponent(id) + '&stamp=' + Date.now();
    }
  }, 5000);
</script>
</body>
</html>
""";

        return page;
    }

    private sealed record Request(
        string Method,
        string Path,
        Dictionary<string, string> Headers,
        Dictionary<string, string> Query);
}
