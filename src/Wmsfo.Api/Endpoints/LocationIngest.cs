using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.Endpoints;

// Contracts 2.5 message path AND POST /locations use the same validate-and-store
// code (contracts 7.2, sql.md 8.2). A37 extends the write path with three
// filters: per-beacon rate limit (min interval), per-fix distance carry (min
// distance), and stored regardless of distance after max gap. The rate limit is
// per node in memory; the carry decision runs inside the transaction against
// the beacon's last stored fix on the event.
public sealed class LocationIngest
{
    private readonly WmsfoConnectionStrings _connections;
    private readonly LiveObjectWriter _writer;
    private readonly NodeStateService _state;
    private readonly WmsfoOptions _options;
    private readonly IServerClock _clock;
    private readonly NodeCounters _counters;
    private readonly ILogger<LocationIngest> _logger;
    private readonly BeaconRateLimiter _rateLimiter;

    public LocationIngest(
        WmsfoConnectionStrings connections,
        LiveObjectWriter writer,
        NodeStateService state,
        WmsfoOptions options,
        IServerClock clock,
        NodeCounters counters,
        BeaconRateLimiter rateLimiter,
        ILogger<LocationIngest> logger)
    {
        _connections = connections;
        _writer = writer;
        _state = state;
        _options = options;
        _clock = clock;
        _counters = counters;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    // The caller has already resolved the beacon from `X-Beacon-Key` (REST) or the
    // hub identity (message path). `throwUnauthorized` chooses between 401 (REST)
    // and 403 (message path) when the beacon lookup inside the transaction fails.
    public async Task<LocationResponse> HandleAsync(
        long beaconId,
        LocationBody body,
        bool messagePath,
        CancellationToken ct)
    {
        Validate(body);

        // A37: pre-transaction rate limit. The `min_interval_ms` used comes
        // from memory: the beacon's override when we have it, else the setting.
        // The limiter is populated by prior accepted fixes on this node.
        var settings = _state.Current.Settings;
        var effectiveInterval = _rateLimiter.EffectiveInterval(beaconId, settings.LocationMinIntervalMs);
        var now = _clock.UtcNow();
        if (_rateLimiter.ShouldDrop(beaconId, now, effectiveInterval, out var lastSeq, out var lastPublished))
        {
            // A37 3(a): no transaction, no live object, no publish. Count the
            // drop and let the flusher push the number to the row within 5 s.
            _counters.IncrementLocationsRateLimited();
            await _rateLimiter.RecordDropAsync(beaconId, _connections.App, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "location accepted seq={Seq} beaconId={BeaconId} eventId={EventId} published={Published} outcome=dropped",
                lastSeq, beaconId, _state.Current.CurrentEvent?.Id, lastPublished);
            return new LocationResponse
            {
                Seq = lastSeq,
                Published = lastPublished,
                ReceivedAt = now,
                ServerTime = now,
                Outcome = "dropped",
            };
        }

        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        long seq;
        DateTimeOffset receivedAt;
        long eventId;
        short statusId;
        long snapshotVersion;
        string snapshotUrl;
        bool published;
        string outcome;                       // 'stored' or 'carried'
        double? bodySpeed = body.SpeedMps;
        double? derivedSpeed = null;

        await using (var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            // sql.md 8.2 step 1: lock the live event row and read next_seq.
            long? lockedEventId = null;
            long nextSeq = 0;
            short lockedStatusId = 0;
            await using (var cmd = new NpgsqlCommand(
                "select id, status_id, next_seq from event where status_id = 3 for update;", conn, tx))
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    lockedEventId = reader.GetInt64(0);
                    lockedStatusId = reader.GetInt16(1);
                    nextSeq = reader.GetInt64(2);
                }
            }
            if (lockedEventId is null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw new ApiException(StatusCodes.Status409Conflict, "no_live_event", "no live event");
            }

            eventId = lockedEventId.Value;
            statusId = lockedStatusId;

            // sql.md 8.2 step 2: re-read is_active, revoked_at, min_interval_ms
            // under the transaction. min_interval_ms is not part of the auth
            // check but the same read carries it back so the row we bill (fixes
            // counters) is the row we validated.
            bool isActive = false;
            DateTimeOffset? revokedAt = null;
            int? beaconMinIntervalMs = null;
            await using (var beacon = new NpgsqlCommand(
                "select is_active, revoked_at, key_version, min_interval_ms from beacon where id = $1;", conn, tx))
            {
                beacon.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                await using var reader = await beacon.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    throw messagePath
                        ? new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "forbidden")
                        : new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthenticated, "unauthenticated");
                }
                isActive = reader.GetBoolean(0);
                revokedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
                beaconMinIntervalMs = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            }
            if (revokedAt is not null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw messagePath
                    ? new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "forbidden")
                    : new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthenticated, "unauthenticated");
            }
            published = isActive;
            _rateLimiter.RecordBeaconMinInterval(beaconId, beaconMinIntervalMs);

            // A37 3(b): read the beacon's last stored fix on this event.
            double? prevLat = null, prevLng = null;
            DateTimeOffset? prevReceivedAt = null;
            DateTimeOffset? prevRecordedAt = null;
            await using (var prev = new NpgsqlCommand(
                @"select seq, lat, lng, recorded_at, received_at
from location where event_id = $1 and beacon_id = $2 order by seq desc limit 1;", conn, tx))
            {
                prev.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                prev.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                await using var reader = await prev.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    prevLat = reader.GetDouble(1);
                    prevLng = reader.GetDouble(2);
                    prevRecordedAt = reader.GetFieldValue<DateTimeOffset>(3);
                    prevReceivedAt = reader.GetFieldValue<DateTimeOffset>(4);
                }
            }

            var minDistance = settings.LocationMinDistanceM;
            var maxGap = TimeSpan.FromSeconds(settings.LocationMaxGapS);
            var nowUtc = DateTimeOffset.UtcNow;

            bool store;
            if (prevLat is not double pLat || prevLng is not double pLng)
            {
                store = true;
            }
            else
            {
                var moved = HaversineMetres(pLat, pLng, body.Lat, body.Lng);
                var pastGap = prevReceivedAt is not DateTimeOffset prAt || (nowUtc - prAt) >= maxGap;
                bool distanceExceeded;
                if (minDistance <= 0)
                {
                    // 0 means "carry only an exact repeat of lat and lng".
                    distanceExceeded = body.Lat != pLat || body.Lng != pLng;
                }
                else
                {
                    distanceExceeded = moved >= minDistance;
                }
                store = distanceExceeded || pastGap;
            }

            bool inserted = false;
            if (store)
            {
                // A37 3(b): compute derived speed when the body did not carry
                // one AND we have a previous stored fix with a positive time
                // delta. speed_source records which source we used.
                string? speedSource;
                if (bodySpeed is double)
                {
                    derivedSpeed = null;
                    speedSource = "beacon";
                }
                else if (prevLat is double pl && prevLng is double png && prevRecordedAt is DateTimeOffset pra)
                {
                    var deltaS = (body.RecordedAt - pra).TotalSeconds;
                    if (deltaS > 0)
                    {
                        var d = HaversineMetres(pl, png, body.Lat, body.Lng);
                        derivedSpeed = d / deltaS;
                        speedSource = "derived";
                    }
                    else
                    {
                        derivedSpeed = null;
                        speedSource = null;
                    }
                }
                else
                {
                    derivedSpeed = null;
                    speedSource = null;
                }

                double? storedSpeed = bodySpeed ?? derivedSpeed;

                // A38: unique (event_id, lat, lng). A repeat of any position
                // already stored in the event goes to the carried outcome; the
                // insert uses `on conflict do nothing` and returning yields no
                // rows on conflict (contracts 4.2, 7.2, sql.md 8.2).
                long? insertedSeq = null;
                DateTimeOffset? insertedReceivedAt = null;
                await using (var insert = new NpgsqlCommand(@"
insert into location (event_id, beacon_id, seq, recorded_at, received_at, lat, lng, speed_mps, speed_source, altitude_m, heading_deg, accuracy_m, published)
values ($1, $2, $3, $4, now(), $5, $6, $7, $8, $9, $10, $11, $12)
on conflict (event_id, lat, lng) do nothing
returning seq, received_at;", conn, tx))
                {
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = nextSeq });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = body.RecordedAt.ToUniversalTime() });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = body.Lat });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = body.Lng });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)storedSpeed ?? DBNull.Value });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)speedSource ?? DBNull.Value });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)body.AltitudeM ?? DBNull.Value });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)body.HeadingDeg ?? DBNull.Value });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)body.AccuracyM ?? DBNull.Value });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = published });
                    await using var reader = await insert.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        insertedSeq = reader.GetInt64(0);
                        insertedReceivedAt = reader.GetFieldValue<DateTimeOffset>(1);
                    }
                }
                inserted = insertedSeq.HasValue;
                if (inserted)
                {
                    seq = insertedSeq!.Value;
                    receivedAt = insertedReceivedAt!.Value;
                }
                else
                {
                    seq = nextSeq;
                    receivedAt = DateTimeOffset.UtcNow;
                }
            }
            else
            {
                seq = nextSeq;
                receivedAt = DateTimeOffset.UtcNow;
            }

            outcome = inserted ? "stored" : "carried";

            // next_seq always advances (both outcomes take a seq).
            await using (var bump = new NpgsqlCommand(
                "update event set next_seq = next_seq + 1, updated_at = now() where id = $1;", conn, tx))
            {
                bump.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                await bump.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (inserted)
            {
                // stored: last_location_at + fixes_stored bumps.
                await using var stamp = new NpgsqlCommand(@"
update beacon
set last_seen_at = now(), last_location_at = now(), stale_since = null,
    fixes_stored = fixes_stored + 1, updated_at = now()
where id = $1;", conn, tx);
                stamp.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                await stamp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                // carried: last_location_at untouched.
                await using var stamp = new NpgsqlCommand(@"
update beacon
set last_seen_at = now(), stale_since = null,
    fixes_carried = fixes_carried + 1, updated_at = now()
where id = $1;", conn, tx);
                stamp.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                await stamp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // sql.md 8.2 step 6: read the snapshot row so the live object built
            // after commit carries the exact snapshotUrl the transaction saw.
            await using (var snap = new NpgsqlCommand(
                "select version, url from snapshot where id = 1;", conn, tx))
            {
                await using var reader = await snap.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    snapshotVersion = reader.GetInt64(0);
                    snapshotUrl = reader.GetString(1);
                }
                else
                {
                    snapshotVersion = 0;
                    snapshotUrl = "";
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        // Accept: set lastAcceptedAt for both stored and carried.
        _rateLimiter.RecordAccept(beaconId, now, seq, published);

        // api.md 16: log `location accepted` with the outcome property. The
        // CloudWatch `LocationPublished` metric filter keys on outcome=stored.
        if (outcome == "stored")
        {
            _counters.IncrementLocationStored();
            if (published) _counters.IncrementLocationPublished();
        }
        else
        {
            _counters.IncrementLocationCarried();
        }
        _logger.LogInformation(
            "location accepted seq={Seq} beaconId={BeaconId} eventId={EventId} published={Published} outcome={Outcome}",
            seq, beaconId, eventId, published, outcome);

        // After commit: on the ingest path, fire the CDN write (contracts 1.8).
        // A37: carried fixes are published just like stored ones - the site
        // sees a rising seq and every apply rule stays as it is.
        if (published)
        {
            var effectiveSpeed = bodySpeed ?? derivedSpeed;
            var fields = new LocationSourceFields(
                EventId: eventId,
                EventStatusId: statusId,
                SnapshotVersion: snapshotVersion,
                SnapshotUrl: snapshotUrl,
                Seq: seq,
                Lat: body.Lat,
                Lng: body.Lng,
                SpeedMps: effectiveSpeed,
                AltitudeM: body.AltitudeM,
                HeadingDeg: body.HeadingDeg,
                AccuracyM: body.AccuracyM,
                RecordedAt: body.RecordedAt.ToUniversalTime(),
                ReceivedAt: receivedAt);
            _ = _writer.WriteForLocationAsync(fields, CancellationToken.None);
        }

        return new LocationResponse
        {
            Seq = seq,
            Published = published,
            ReceivedAt = receivedAt,
            ServerTime = _clock.UtcNow(),
            Outcome = outcome,
        };
    }

    // Contracts 4.2 field table.
    public static void Validate(LocationBody body)
    {
        var v = new RequestValidation();
        if (double.IsNaN(body.Lat) || double.IsInfinity(body.Lat) || body.Lat < -90 || body.Lat > 90)
            v.Field("lat", "must be between -90 and 90");
        if (double.IsNaN(body.Lng) || double.IsInfinity(body.Lng) || body.Lng < -180 || body.Lng > 180)
            v.Field("lng", "must be between -180 and 180");
        if (body.RecordedAt == default)
            v.Field("recordedAt", "required");
        if (body.SpeedMps is double sp && (double.IsNaN(sp) || double.IsInfinity(sp) || sp < 0 || sp > 500))
            v.Field("speedMps", "must be between 0 and 500");
        if (body.AltitudeM is double alt && (double.IsNaN(alt) || double.IsInfinity(alt) || alt < -500 || alt > 20000))
            v.Field("altitudeM", "must be between -500 and 20000");
        if (body.HeadingDeg is double hd && (double.IsNaN(hd) || double.IsInfinity(hd) || hd < 0 || hd > 360))
            v.Field("headingDeg", "must be between 0 and 360");
        if (body.AccuracyM is double ac && (double.IsNaN(ac) || double.IsInfinity(ac) || ac < 0 || ac > 100000))
            v.Field("accuracyM", "must be between 0 and 100000");
        v.ThrowIfInvalid();
    }

    // Great-circle distance in metres; the earth radius is the IUGG mean.
    public static double HaversineMetres(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371008.8;
        var toRad = Math.PI / 180.0;
        var dLat = (lat2 - lat1) * toRad;
        var dLng = (lng2 - lng1) * toRad;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * toRad) * Math.Cos(lat2 * toRad)
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }
}

// A37: per-node in-memory rate limiter for POST /locations and the message
// path. Each beacon tracks the last accepted timestamp, its effective override
// (mirrored from the beacon row on the last write), the last accepted seq for
// the dropped-response body, and a drop counter that flushes to
// beacon.fixes_rate_limited at most once every 5 s per beacon (contracts 7.2).
public sealed class BeaconRateLimiter
{
    private readonly ConcurrentDictionary<long, BeaconState> _states = new();

    private sealed class BeaconState
    {
        public DateTimeOffset LastAcceptedAt = DateTimeOffset.MinValue;
        public long LastSeq = 0;
        public bool LastPublished = false;
        public int? OverrideMs;
        public int PendingDrops = 0;
        public DateTimeOffset LastFlushAt = DateTimeOffset.MinValue;
        public readonly SemaphoreSlim FlushGate = new(1, 1);
    }

    private BeaconState GetOrAdd(long beaconId) =>
        _states.GetOrAdd(beaconId, _ => new BeaconState());

    public int EffectiveInterval(long beaconId, int settingMs)
    {
        var s = GetOrAdd(beaconId);
        return s.OverrideMs ?? settingMs;
    }

    public bool ShouldDrop(long beaconId, DateTimeOffset now, int effectiveMs, out long lastSeq, out bool lastPublished)
    {
        var s = GetOrAdd(beaconId);
        lastSeq = Interlocked.Read(ref s.LastSeq);
        lastPublished = s.LastPublished;
        if (effectiveMs <= 0) return false;
        if (s.LastAcceptedAt == DateTimeOffset.MinValue) return false;
        var since = now - s.LastAcceptedAt;
        return since < TimeSpan.FromMilliseconds(effectiveMs);
    }

    public void RecordAccept(long beaconId, DateTimeOffset now, long seq, bool published)
    {
        var s = GetOrAdd(beaconId);
        s.LastAcceptedAt = now;
        Interlocked.Exchange(ref s.LastSeq, seq);
        s.LastPublished = published;
    }

    // Test hook: reset `lastAcceptedAt` so a subsequent fix is not dropped by
    // the min-interval filter, without wiping the beacon's seq / published /
    // override state.
    public void ResetLastAccepted(long beaconId)
    {
        var s = GetOrAdd(beaconId);
        s.LastAcceptedAt = DateTimeOffset.MinValue;
    }

    public void RecordBeaconMinInterval(long beaconId, int? overrideMs)
    {
        var s = GetOrAdd(beaconId);
        s.OverrideMs = overrideMs;
    }

    // Called by the drop branch. Increments the pending count and, when at
    // least 5 s have passed since the last flush, sends the accumulated count
    // to the row and resets. One update per beacon at most every 5 s.
    public async Task RecordDropAsync(long beaconId, string appConnectionString, CancellationToken ct)
    {
        var s = GetOrAdd(beaconId);
        Interlocked.Increment(ref s.PendingDrops);
        var now = DateTimeOffset.UtcNow;
        if (now - s.LastFlushAt < TimeSpan.FromSeconds(5)) return;

        if (!await s.FlushGate.WaitAsync(0, ct).ConfigureAwait(false))
            return;                    // another flush in flight
        try
        {
            if (DateTimeOffset.UtcNow - s.LastFlushAt < TimeSpan.FromSeconds(5))
                return;
            var toFlush = Interlocked.Exchange(ref s.PendingDrops, 0);
            if (toFlush <= 0) return;
            try
            {
                await using var conn = new NpgsqlConnection(appConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand(
                    "update beacon set fixes_rate_limited = fixes_rate_limited + $1, updated_at = now() where id = $2;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (long)toFlush });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                s.LastFlushAt = DateTimeOffset.UtcNow;
            }
            catch
            {
                // Return the drops to the pending counter so a later flush
                // captures them; a persistent database failure keeps the row
                // untouched and the memory counter growing.
                Interlocked.Add(ref s.PendingDrops, toFlush);
            }
        }
        finally
        {
            s.FlushGate.Release();
        }
    }
}
