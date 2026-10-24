using System.Net.Http;
using System.Text.Json;

namespace GhostScreen.Core;

public sealed record StatusEndpoint(string Url, string Token);

public sealed record StatusTarget(
    string Id,
    string Title,
    string Kind,
    string State,
    string? DegradedReason);

public sealed record StatusSnapshot(
    string Version,
    int Pid,
    long UptimeSeconds,
    int TargetCount,
    string RawJson,
    IReadOnlyList<StatusTarget> Targets);

public sealed record PanicResult(int Closed);

/// <summary>
/// Client for the loopback status endpoint. Used by the tray app and tests.
/// The endpoint file is written by the MCP process; the API uses a header token
/// so the token does not need to be repeated in every URL.
/// </summary>
public sealed class StatusClient : IDisposable
{
    private readonly StatusEndpoint _endpoint;
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public StatusClient(StatusEndpoint endpoint, HttpClient? http = null)
    {
        _endpoint = endpoint;
        _baseUri = new Uri(new Uri(endpoint.Url).GetLeftPart(UriPartial.Authority));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        _ownsHttp = http is null;
    }

    public string PageUrl => _endpoint.Url;

    public static string DefaultEndpointPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GhostScreen",
        "status.json");

    public static StatusEndpoint? TryReadEndpoint(string? path = null)
    {
        var file = path ?? DefaultEndpointPath;

        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            var token = root.TryGetProperty("token", out var tokenElement) ? tokenElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(url) ||
                string.IsNullOrWhiteSpace(token) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp ||
                uri.Host != "127.0.0.1")
            {
                return null;
            }

            return new StatusEndpoint(url, token);
        }
        catch
        {
            return null;
        }
    }

    public async Task<StatusSnapshot?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "/api/status"));
            request.Headers.Add("X-GhostScreen-Token", _endpoint.Token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var targets = new List<StatusTarget>();
            if (root.TryGetProperty("targets", out var targetsElement) &&
                targetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var target in targetsElement.EnumerateArray())
                {
                    targets.Add(new StatusTarget(
                        target.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                        target.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                        target.TryGetProperty("kind", out var kind) ? kind.GetString() ?? string.Empty : string.Empty,
                        target.TryGetProperty("state", out var state) ? state.GetString() ?? string.Empty : string.Empty,
                        target.TryGetProperty("degraded_reason", out var degradedReason) &&
                        degradedReason.ValueKind == JsonValueKind.String
                            ? degradedReason.GetString()
                            : null));
                }
            }

            return new StatusSnapshot(
                root.TryGetProperty("version", out var version) ? version.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var pidValue) ? pidValue : 0,
                root.TryGetProperty("uptime_seconds", out var uptime) && uptime.TryGetInt64(out var uptimeValue)
                    ? uptimeValue
                    : 0,
                targets.Count,
                json,
                targets);
        }
        catch
        {
            return null;
        }
    }

    public async Task<PanicResult?> PanicAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "/api/panic"));
            request.Headers.Add("X-GhostScreen-Token", _endpoint.Token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return new PanicResult(
                document.RootElement.TryGetProperty("closed", out var closed) && closed.TryGetInt32(out var value)
                    ? value
                    : 0);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> CloseTargetAsync(string id, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = $"/api/close_target?target_id={Uri.EscapeDataString(id)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, path));
            request.Headers.Add("X-GhostScreen-Token", _endpoint.Token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("ok", out var ok) &&
                   ok.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
