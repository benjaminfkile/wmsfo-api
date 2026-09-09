using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Realtime;

// api.md 12.4: one HttpClient with base address WMSFO_GATEWAY_INTERNAL_URL and
// header X-Gateway-Realtime-Token = GATEWAY_REALTIME_TOKEN. `publish` splices the
// exact bytes the caller PUT so the hub and CDN carry identical bytes (no second
// JSON serialization). When the token is absent every call is a no-op that logs
// once at startup.
public interface IGatewayInternalClient
{
    // POST /internal/publish { channel, event, payload }; payload is the raw bytes.
    Task<bool> PublishAsync(string channel, string @event, ReadOnlyMemory<byte> payloadBytes, CancellationToken ct);

    // GET /internal/leader; the answer decides section 13's IsLeader.
    Task<LeaderAnswer> GetLeaderAsync(CancellationToken ct);

    // GET /internal/presence/<service>:ingest; returned identities set hubConnected.
    Task<IReadOnlyList<string>?> GetPresenceAsync(string channel, CancellationToken ct);

    // The last instance id the gateway told us about (returned by /internal/leader).
    // Surfaced by GET /admin/live.node.instance.
    string? LastInstanceId { get; }
}

public sealed record LeaderAnswer(bool IsLeader, DateTimeOffset? EvaluatedAt, string? InstanceId, bool Reachable);

public sealed class GatewayInternalClient : IGatewayInternalClient, IDisposable
{
    public const string TokenHeaderName = "X-Gateway-Realtime-Token";
    public const string PublishPath = "/internal/publish";
    public const string LeaderPath = "/internal/leader";
    public const string PresencePathPrefix = "/internal/presence/";

    public static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LeaderTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly ILogger<GatewayInternalClient> _logger;
    private readonly bool _enabled;
    private string? _lastInstanceId;

    public GatewayInternalClient(HttpClient http, WmsfoOptions options, ILogger<GatewayInternalClient> logger)
    {
        _http = http;
        _logger = logger;
        _http.BaseAddress = new Uri(options.GatewayInternalUrl);
        if (!string.IsNullOrEmpty(options.GatewayRealtimeToken))
        {
            _http.DefaultRequestHeaders.Add(TokenHeaderName, options.GatewayRealtimeToken);
            _enabled = true;
        }
        else
        {
            _enabled = false;
            _logger.LogInformation(
                "GATEWAY_REALTIME_TOKEN is not set; gateway publish, leader, and presence calls are no-ops.");
        }
    }

    public string? LastInstanceId => _lastInstanceId;

    public async Task<bool> PublishAsync(string channel, string @event, ReadOnlyMemory<byte> payloadBytes, CancellationToken ct)
    {
        if (!_enabled) return false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PublishTimeout);

            // Assemble the body manually so the payload bytes are spliced in as
            // raw JSON — api.md 12.4 forbids a second SerializeToUtf8Bytes here so
            // the CDN copy and the hub copy are byte-identical.
            using var buffer = new MemoryStream(payloadBytes.Length + 128);
            var channelJson = JsonSerializer.Serialize(channel);
            var eventJson = JsonSerializer.Serialize(@event);
            var prefix = $"{{\"channel\":{channelJson},\"event\":{eventJson},\"payload\":";
            buffer.Write(System.Text.Encoding.UTF8.GetBytes(prefix));
            buffer.Write(payloadBytes.Span);
            buffer.WriteByte((byte)'}');
            buffer.Position = 0;

            using var content = new StreamContent(buffer);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var response = await _http.PostAsync(PublishPath, content, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "gateway publish {Channel}/{Event} failed: status={Status} marker={Marker}",
                    channel, @event, (int)response.StatusCode, LogMarkers.PublishFailed);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "gateway publish {Channel}/{Event} threw; marker={Marker}", channel, @event, LogMarkers.PublishFailed);
            return false;
        }
    }

    public async Task<LeaderAnswer> GetLeaderAsync(CancellationToken ct)
    {
        if (!_enabled) return new LeaderAnswer(false, null, null, Reachable: false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(LeaderTimeout);
            using var response = await _http.GetAsync(LeaderPath, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new LeaderAnswer(false, null, null, Reachable: false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            bool isLeader = false;
            DateTimeOffset? evaluatedAt = null;
            string? instanceId = null;
            if (doc.RootElement.TryGetProperty("isLeader", out var flag) && flag.ValueKind == JsonValueKind.True)
                isLeader = true;
            if (doc.RootElement.TryGetProperty("evaluatedAt", out var ts)
                && ts.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ts.GetString(), out var parsed))
            {
                evaluatedAt = parsed.ToUniversalTime();
            }
            if (doc.RootElement.TryGetProperty("instanceId", out var id) && id.ValueKind == JsonValueKind.String)
            {
                instanceId = id.GetString();
                _lastInstanceId = instanceId;
            }
            return new LeaderAnswer(isLeader, evaluatedAt, instanceId, Reachable: true);
        }
        catch (Exception)
        {
            return new LeaderAnswer(false, null, null, Reachable: false);
        }
    }

    public async Task<IReadOnlyList<string>?> GetPresenceAsync(string channel, CancellationToken ct)
    {
        if (!_enabled) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PresenceTimeout);
            using var response = await _http.GetAsync(PresencePathPrefix + Uri.EscapeDataString(channel), cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var identities = new List<string>();
            if (doc.RootElement.TryGetProperty("identities", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String) identities.Add(e.GetString() ?? "");
                }
            }
            return identities;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
