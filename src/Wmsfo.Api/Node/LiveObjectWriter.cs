using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.Node;

// api.md 10.1: one writer per node. Single-flight with coalescing:
// a caller that finds a write in flight sets a pending flag and returns; the
// writer loops while pending is set, each iteration building from the state that
// is current at that moment. Two entry points (WriteForLocation, WriteFromState).
public sealed class LiveObjectWriter
{
    public const string CdnKey = "live/location.json";
    public const string ContentType = "application/json; charset=utf-8";
    public const string CacheControl = "s-maxage=1, max-age=0";
    public static readonly TimeSpan LocationPutTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan AdminPutTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan AdminRetryDelay = TimeSpan.FromSeconds(1);
    public const int AdminAttempts = 3;

    private readonly IObjectStore _store;
    private readonly IGatewayInternalClient _gateway;
    private readonly NodeStateService _state;
    private readonly WmsfoConnectionStrings _connections;
    private readonly WmsfoOptions _options;
    private readonly ILogger<LiveObjectWriter> _logger;

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private volatile bool _pending;
    private volatile string _pendingReason = "";
    private volatile LocationSourceFields? _pendingLocation;

    // Set by the writer for its last successful build, so tests can assert the
    // bytes written to the CDN equal the bytes handed to the gateway publish.
    public byte[]? LastWrittenBytes { get; private set; }
    public LiveObject? LastWrittenObject { get; private set; }

    public LiveObjectWriter(
        IObjectStore store,
        IGatewayInternalClient gateway,
        NodeStateService state,
        WmsfoConnectionStrings connections,
        WmsfoOptions options,
        ILogger<LiveObjectWriter> logger)
    {
        _store = store;
        _gateway = gateway;
        _state = state;
        _connections = connections;
        _options = options;
        _logger = logger;
    }

    // Called by the location handler after its response is queued (api.md 10.1).
    // Builds from the fields the transaction read plus in-memory tally and settings.
    public Task WriteForLocationAsync(LocationSourceFields fields, CancellationToken ct) =>
        RunAsync(reason: "location", location: fields, adminPath: false, ct);

    // Called after an admin commit, moderation while live, tick rewrite, republish, first boot.
    public Task WriteFromStateAsync(string reason, CancellationToken ct) =>
        RunAsync(reason, location: null, adminPath: true, ct);

    private async Task RunAsync(string reason, LocationSourceFields? location, bool adminPath, CancellationToken ct)
    {
        // Coalesce: if a write is in flight, the current attempt just sets a flag
        // and the running writer will loop with the new state.
        if (!await _semaphore.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _pending = true;
            _pendingReason = reason;
            if (location is not null) _pendingLocation = location;
            return;
        }

        try
        {
            do
            {
                _pending = false;
                var currentReason = reason;
                var currentLocation = location;
                var currentAdminPath = adminPath;

                if (currentAdminPath)
                {
                    // WriteFromState refreshes state first (api.md 10.1).
                    await _state.RefreshAsync(currentReason, ct).ConfigureAwait(false);
                }

                var (obj, bytes) = Build(_state.Current, currentLocation);
                LastWrittenObject = obj;
                LastWrittenBytes = bytes;

                var putOk = await PutAsync(bytes, currentAdminPath, ct).ConfigureAwait(false);
                // Publish is attempted whether or not the PUT succeeded (api.md 10.1).
                _ = await _gateway.PublishAsync(_options.ServiceName + ":location", "location", bytes, ct).ConfigureAwait(false);
                if (currentLocation is not null) _state.MarkWroteForLocation();
                await UpdateLiveStateAsync(putOk, obj, ct).ConfigureAwait(false);

                if (_pending)
                {
                    reason = _pendingReason;
                    location = _pendingLocation;
                    _pendingLocation = null;
                    adminPath = true;   // Coalesced writes always refresh from SQL.
                }
            }
            while (_pending);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // api.md 10.1: PUT live/location.json with the immutable-cache-control-esque
    // header for the live object; the location path is one attempt with 3 s
    // timeout, the admin path is up to three attempts one second apart.
    private async Task<bool> PutAsync(byte[] bytes, bool adminPath, CancellationToken ct)
    {
        var attempts = adminPath ? AdminAttempts : 1;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(adminPath ? AdminPutTimeout : LocationPutTimeout);
                await _store.PutObjectAsync(CdnKey, bytes, ContentType, CacheControl, tag: null, cts.Token)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "live PUT attempt {Attempt}/{Attempts} failed; marker=wmsfo_live_put_failed",
                    attempt, attempts);
                if (attempt < attempts)
                {
                    try { await Task.Delay(AdminRetryDelay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return false; }
                }
            }
        }
        return false;
    }

    private async Task UpdateLiveStateAsync(bool putOk, LiveObject obj, CancellationToken ct)
    {
        // sql.md 8.18. Failure to update live_state itself is swallowed; it is a
        // telemetry table.
        try
        {
            await using var conn = new NpgsqlConnection(_connections.App);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            if (putOk)
            {
                await using var cmd = new NpgsqlCommand(@"
update live_state
set last_write_at = now(),
    last_write_seq = $1,
    last_write_version = $2,
    last_write_node = $3,
    last_write_error = null
where id = 1;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)obj.Seq ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = obj.EventStatusId is null ? DBNull.Value : (object)_state.Current.SnapshotVersion });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)_options.ServiceName ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await using var cmd = new NpgsqlCommand(@"
update live_state
set last_write_error = $1,
    last_write_node = $2
where id = 1;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "live_put_failed" });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)_options.ServiceName ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "live_state update failed");
        }
    }

    // Testable pure builder. Given a state snapshot and optional per-location
    // fields (from the location transaction), produces the LiveObject and its
    // canonical bytes (contracts 1.2, 1.6).
    public (LiveObject Object, byte[] Bytes) Build(NodeSnapshot state, LocationSourceFields? location)
    {
        var now = DateTimeOffset.UtcNow;
        return Build(state, location, publishedAt: now);
    }

    public (LiveObject Object, byte[] Bytes) Build(NodeSnapshot state, LocationSourceFields? location, DateTimeOffset publishedAt)
    {
        var obj = new LiveObject
        {
            SchemaVersion = 1,
            EventId = state.CurrentEvent?.Id,
            EventStatusId = state.CurrentEvent?.StatusId,
            PollIntervalMs = state.Settings.PollIntervalMs,
            SnapshotUrl = state.SnapshotUrl,
            CookieTally = BuildTally(state),
            Seq = null,
            Lat = null,
            Lng = null,
            SpeedMps = null,
            AltitudeM = null,
            HeadingDeg = null,
            AccuracyM = null,
            RecordedAt = null,
            ReceivedAt = null,
            PublishedAt = publishedAt,
        };

        // Prefer the transaction-local fields when provided; otherwise the state's
        // latest published location for the current event.
        if (location is not null && location.EventId == state.CurrentEvent?.Id)
        {
            obj.Seq = location.Seq;
            obj.Lat = location.Lat;
            obj.Lng = location.Lng;
            obj.SpeedMps = location.SpeedMps;
            obj.AltitudeM = location.AltitudeM;
            obj.HeadingDeg = location.HeadingDeg;
            obj.AccuracyM = location.AccuracyM;
            obj.RecordedAt = location.RecordedAt;
            obj.ReceivedAt = location.ReceivedAt;
        }
        else if (state.LatestPublished is { } p)
        {
            obj.Seq = p.Seq;
            obj.Lat = p.Lat;
            obj.Lng = p.Lng;
            obj.SpeedMps = p.SpeedMps;
            obj.AltitudeM = p.AltitudeM;
            obj.HeadingDeg = p.HeadingDeg;
            obj.AccuracyM = p.AccuracyM;
            obj.RecordedAt = p.RecordedAt;
            obj.ReceivedAt = p.ReceivedAt;
        }

        var bytes = CanonicalJson.SerializeToUtf8Bytes(obj);
        return (obj, bytes);
    }

    private static SortedDictionary<long, int> BuildTally(NodeSnapshot state)
    {
        // status 4: use event.final_cookie_tally verbatim; otherwise the in-memory tally.
        if (state.CurrentEvent is { StatusId: 4, FinalCookieTally: not null } ended)
        {
            var frozen = new SortedDictionary<long, int>();
            foreach (var kv in ended.FinalCookieTally) frozen[kv.Key] = kv.Value;
            return frozen;
        }
        var result = new SortedDictionary<long, int>();
        foreach (var kv in state.CookieTally) result[kv.Key] = kv.Value;
        return result;
    }
}

// The event-status-and-snapshot slice a location transaction reads; the writer
// prefers these values over what memory shows because the transaction saw them
// under lock.
public sealed record LocationSourceFields(
    long EventId,
    short EventStatusId,
    long SnapshotVersion,
    string SnapshotUrl,
    long Seq,
    double Lat,
    double Lng,
    double? SpeedMps,
    double? AltitudeM,
    double? HeadingDeg,
    double? AccuracyM,
    DateTimeOffset RecordedAt,
    DateTimeOffset ReceivedAt);
