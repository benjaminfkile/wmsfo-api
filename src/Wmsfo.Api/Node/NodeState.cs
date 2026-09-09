using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Node;

// api.md 8: `NodeState` singleton per node holds the immutable record of contracts 7.1.
// Refresh runs the six statements of sql.md 8.17 in one batched command on the app
// connection and swaps the record with Interlocked.Exchange so readers never see a
// half-updated view. The TallyDelta counter is the bridge between a POST /cookies
// insert and the next tick's SQL count on the inserting node.
public sealed class NodeStateService
{
    private readonly string _appConnectionString;
    private readonly ILogger<NodeStateService> _logger;
    private readonly ConcurrentDictionary<long, int> _tallyDelta = new();
    private readonly object _leaderLock = new();
    private readonly object _flagsLock = new();
    private NodeSnapshot _current = NodeSnapshot.Empty;
    private DateTimeOffset _lastSettingsReadAt = DateTimeOffset.MinValue;
    private long _lastWrittenVersion;
    private bool _wroteForLocationSinceVersionChange;
    private LeaderStatus _leader = LeaderStatus.Follower(DateTimeOffset.MinValue);

    public NodeStateService(WmsfoConnectionStrings connectionStrings, ILogger<NodeStateService> logger)
    {
        _appConnectionString = connectionStrings.App;
        _logger = logger;
    }

    // The current snapshot, replaced atomically by RefreshAsync. Readers take one snapshot per request.
    public NodeSnapshot Current => Volatile.Read(ref _current);

    // Section 13: leader status is written by LeaderMonitor and read by ChoreHost + diagnostics.
    public LeaderStatus Leader
    {
        get { lock (_leaderLock) return _leader; }
    }

    public void SetLeader(LeaderStatus status)
    {
        lock (_leaderLock) _leader = status;
    }

    public long LastWrittenVersion
    {
        get { lock (_flagsLock) return _lastWrittenVersion; }
    }

    public bool WroteForLocationSinceVersionChange
    {
        get { lock (_flagsLock) return _wroteForLocationSinceVersionChange; }
    }

    public void RecordWroteVersion(long version)
    {
        lock (_flagsLock) _lastWrittenVersion = version;
    }

    public void MarkWroteForLocation()
    {
        lock (_flagsLock) _wroteForLocationSinceVersionChange = true;
    }

    public void ClearWroteForLocation()
    {
        lock (_flagsLock) _wroteForLocationSinceVersionChange = false;
    }

    // api.md 8: POST /cookies increments this so the next tick's SQL count folds it in.
    public void IncrementTallyDelta(long cookieTypeId)
    {
        _tallyDelta.AddOrUpdate(cookieTypeId, 1, (_, v) => v + 1);
    }

    // Snapshot and clear the current delta; called by RefreshAsync while folding the SQL tally.
    private ImmutableDictionary<long, int> DrainTallyDelta()
    {
        if (_tallyDelta.IsEmpty) return ImmutableDictionary<long, int>.Empty;
        var drained = ImmutableDictionary.CreateBuilder<long, int>();
        foreach (var kv in _tallyDelta)
        {
            if (_tallyDelta.TryRemove(kv.Key, out var value) && value > 0)
                drained[kv.Key] = value;
        }
        return drained.ToImmutable();
    }

    // sql.md 8.17: six statements in one batched command on the app connection.
    public async Task<NodeSnapshot> RefreshAsync(string reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = Volatile.Read(ref _current);
        var readSettings = ShouldReadSettings(previous, now);

        await using var conn = new NpgsqlConnection(_appConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        long snapshotVersion = 0;
        string snapshotUrl = "";
        CurrentEvent? currentEvent = null;
        long? activeBeaconId = null;
        PublishedLocation? latestPublished = null;
        var tallyBuilder = new SortedDictionary<long, int>();
        var settings = previous.Settings;

        // Six statements batched. The middle three depend on the current event id
        // which we read from statement 2, so we do a second batch for those.

        var batch = new NpgsqlBatch(conn);
        batch.BatchCommands.Add(new NpgsqlBatchCommand(
            "select version, url from snapshot where id = 1;"));
        batch.BatchCommands.Add(new NpgsqlBatchCommand(
            "select id, status_id, final_cookie_tally from event where is_current;"));
        batch.BatchCommands.Add(new NpgsqlBatchCommand(
            "select id from beacon where is_active;"));
        if (readSettings)
        {
            batch.BatchCommands.Add(new NpgsqlBatchCommand(
                "select key, value from app_setting;"));
        }

        await using (var reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            // snapshot
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                snapshotVersion = reader.GetInt64(0);
                snapshotUrl = reader.GetString(1);
            }
            await reader.NextResultAsync(ct).ConfigureAwait(false);

            // current event
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                var statusId = reader.GetInt16(1);
                ImmutableDictionary<long, int>? finalCookieTally = null;
                if (!reader.IsDBNull(2))
                {
                    var json = reader.GetString(2);
                    finalCookieTally = ParseCookieTally(json);
                }
                currentEvent = new CurrentEvent(id, statusId, finalCookieTally);
            }
            await reader.NextResultAsync(ct).ConfigureAwait(false);

            // active beacon
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                activeBeaconId = reader.GetInt64(0);
            }

            if (readSettings)
            {
                await reader.NextResultAsync(ct).ConfigureAwait(false);
                var appSettings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var key = reader.GetString(0);
                    var value = reader.GetString(1);
                    using var doc = JsonDocument.Parse(value);
                    appSettings[key] = doc.RootElement.Clone();
                }
                settings = NodeSettings.FromMap(appSettings);
            }
        }

        // The published-location and cookie-tally reads depend on the current event id.
        if (currentEvent is not null)
        {
            var eventId = currentEvent.Id;
            var second = new NpgsqlBatch(conn);
            second.BatchCommands.Add(new NpgsqlBatchCommand(@"
                select seq, lat, lng, speed_mps, altitude_m, heading_deg, accuracy_m, recorded_at, received_at
                from location
                where event_id = $1 and published
                order by seq desc
                limit 1;")
            {
                Parameters = { new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId } },
            });
            second.BatchCommands.Add(new NpgsqlBatchCommand(@"
                select cookie_type_id, count(*)
                from cookie
                where event_id = $1 and hidden_at is null
                group by cookie_type_id;")
            {
                Parameters = { new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId } },
            });

            await using var reader = await second.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                latestPublished = new PublishedLocation(
                    Seq: reader.GetInt64(0),
                    Lat: reader.GetDouble(1),
                    Lng: reader.GetDouble(2),
                    SpeedMps: reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    AltitudeM: reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    HeadingDeg: reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    AccuracyM: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    RecordedAt: reader.GetFieldValue<DateTimeOffset>(7),
                    ReceivedAt: reader.GetFieldValue<DateTimeOffset>(8));
            }
            await reader.NextResultAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var typeId = reader.GetInt64(0);
                var count = (int)reader.GetInt64(1);
                if (count > 0) tallyBuilder[typeId] = count;
            }
        }

        // Fold the per-node delta (only counts that are not in SQL yet).
        var drained = DrainTallyDelta();
        foreach (var kv in drained)
        {
            if (tallyBuilder.TryGetValue(kv.Key, out var existing))
                tallyBuilder[kv.Key] = existing + kv.Value;
            else
                tallyBuilder[kv.Key] = kv.Value;
        }

        var refreshed = new NodeSnapshot(
            SnapshotVersion: snapshotVersion,
            SnapshotUrl: snapshotUrl,
            CurrentEvent: currentEvent,
            ActiveBeaconId: activeBeaconId,
            LatestPublished: latestPublished,
            CookieTally: tallyBuilder.ToImmutableSortedDictionary(),
            Settings: settings,
            RefreshedAt: now);

        Interlocked.Exchange(ref _current, refreshed);
        if (readSettings) _lastSettingsReadAt = now;
        _logger.LogDebug("node state refreshed: {Reason} version={Version}", reason, snapshotVersion);
        return refreshed;
    }

    private bool ShouldReadSettings(NodeSnapshot previous, DateTimeOffset now)
    {
        // First refresh (RefreshedAt == MinValue) always reads settings so the node
        // starts with real values. Later refreshes re-read on version change or
        // after 5 s (api.md 9).
        if (previous.RefreshedAt == DateTimeOffset.MinValue) return true;
        if (previous.SnapshotVersion != Volatile.Read(ref _current).SnapshotVersion) return true;
        return now - _lastSettingsReadAt >= TimeSpan.FromSeconds(5);
    }

    private static ImmutableDictionary<long, int> ParseCookieTally(string json)
    {
        // event.final_cookie_tally is jsonb: {"1": 412, "3": 90}
        using var doc = JsonDocument.Parse(json);
        var builder = ImmutableDictionary.CreateBuilder<long, int>();
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (long.TryParse(entry.Name, out var key)
                && entry.Value.ValueKind == JsonValueKind.Number
                && entry.Value.TryGetInt32(out var count))
            {
                builder[key] = count;
            }
        }
        return builder.ToImmutable();
    }
}

// The immutable record replaced atomically by Refresh.
public sealed record NodeSnapshot(
    long SnapshotVersion,
    string SnapshotUrl,
    CurrentEvent? CurrentEvent,
    long? ActiveBeaconId,
    PublishedLocation? LatestPublished,
    ImmutableSortedDictionary<long, int> CookieTally,
    NodeSettings Settings,
    DateTimeOffset RefreshedAt)
{
    public static NodeSnapshot Empty { get; } = new(
        SnapshotVersion: 0,
        SnapshotUrl: "",
        CurrentEvent: null,
        ActiveBeaconId: null,
        LatestPublished: null,
        CookieTally: ImmutableSortedDictionary<long, int>.Empty,
        Settings: NodeSettings.Defaults,
        RefreshedAt: DateTimeOffset.MinValue);
}

public sealed record CurrentEvent(long Id, short StatusId, ImmutableDictionary<long, int>? FinalCookieTally);

public sealed record PublishedLocation(
    long Seq,
    double Lat,
    double Lng,
    double? SpeedMps,
    double? AltitudeM,
    double? HeadingDeg,
    double? AccuracyM,
    DateTimeOffset RecordedAt,
    DateTimeOffset ReceivedAt);

// contracts 6: the five knobs, with defaults filled in for a missing row.
public sealed record NodeSettings(
    int PollIntervalMs,
    int CookieLimitPerPerson,
    int SponsorLingerMsPerDollar,
    int SponsorLingerMinMs,
    int BeaconStaleAfterS)
{
    public static NodeSettings Defaults { get; } = new(
        PollIntervalMs: 5000,
        CookieLimitPerPerson: 10,
        SponsorLingerMsPerDollar: 40,
        SponsorLingerMinMs: 2000,
        BeaconStaleAfterS: 45);

    public static NodeSettings FromMap(IReadOnlyDictionary<string, JsonElement> map)
    {
        int Read(string key, int fallback)
        {
            if (map.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var parsed))
                return parsed;
            return fallback;
        }
        var d = Defaults;
        return new NodeSettings(
            PollIntervalMs: Read("poll_interval_ms", d.PollIntervalMs),
            CookieLimitPerPerson: Read("cookie_limit_per_person", d.CookieLimitPerPerson),
            SponsorLingerMsPerDollar: Read("sponsor_linger_ms_per_dollar", d.SponsorLingerMsPerDollar),
            SponsorLingerMinMs: Read("sponsor_linger_min_ms", d.SponsorLingerMinMs),
            BeaconStaleAfterS: Read("beacon_stale_after_s", d.BeaconStaleAfterS));
    }
}

// section 13: leadership. IsLeader true only when the last answer was 2xx,
// isLeader=true, and evaluatedAt is < 90 s old at evaluation time. The gateway
// re-evaluates leadership on its reconcile loop (every 30 s plus jitter), so the
// window covers two missed loops. Read at request time this expires on its own
// even if the poller stalled.
public sealed record LeaderStatus(bool IsLeader, DateTimeOffset EvaluatedAt, string? InstanceId)
{
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(90);

    public bool IsCurrentlyLeader(DateTimeOffset now)
    {
        if (!IsLeader) return false;
        if (EvaluatedAt == DateTimeOffset.MinValue) return false;
        return now - EvaluatedAt < Expiry;
    }

    public static LeaderStatus Follower(DateTimeOffset evaluatedAt, string? instanceId = null) =>
        new(false, evaluatedAt, instanceId);
}
