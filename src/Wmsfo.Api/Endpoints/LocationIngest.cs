using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.Endpoints;

// Contracts 2.5 message path AND POST /locations use the same validate-and-store
// code. The transaction is sql.md 8.2. After commit the caller writes the live
// object (contracts 1.8) when the row was `published`.
public sealed class LocationIngest
{
    private readonly WmsfoConnectionStrings _connections;
    private readonly LiveObjectWriter _writer;
    private readonly NodeStateService _state;
    private readonly WmsfoOptions _options;
    private readonly IServerClock _clock;
    private readonly NodeCounters _counters;
    private readonly ILogger<LocationIngest> _logger;

    public LocationIngest(
        WmsfoConnectionStrings connections,
        LiveObjectWriter writer,
        NodeStateService state,
        WmsfoOptions options,
        IServerClock clock,
        NodeCounters counters,
        ILogger<LocationIngest> logger)
    {
        _connections = connections;
        _writer = writer;
        _state = state;
        _options = options;
        _clock = clock;
        _counters = counters;
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

        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        long seq;
        DateTimeOffset receivedAt;
        long eventId;
        short statusId;
        long snapshotVersion;
        string snapshotUrl;
        bool published;

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

            // sql.md 8.2 step 2: re-read is_active, revoked_at, key_version under the transaction.
            bool isActive = false;
            DateTimeOffset? revokedAt = null;
            await using (var beacon = new NpgsqlCommand(
                "select is_active, revoked_at, key_version from beacon where id = $1;", conn, tx))
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
            }
            if (revokedAt is not null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw messagePath
                    ? new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "forbidden")
                    : new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthenticated, "unauthenticated");
            }
            published = isActive;

            // sql.md 8.2 step 3: insert location, returning seq, received_at.
            await using (var insert = new NpgsqlCommand(@"
insert into location (event_id, beacon_id, seq, recorded_at, received_at, lat, lng, speed_mps, altitude_m, heading_deg, accuracy_m, published)
values ($1, $2, $3, $4, now(), $5, $6, $7, $8, $9, $10, $11)
returning seq, received_at;", conn, tx))
            {
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = nextSeq });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = body.RecordedAt.ToUniversalTime() });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = body.Lat });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = body.Lng });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)body.SpeedMps ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)body.AltitudeM ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)body.HeadingDeg ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)body.AccuracyM ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = published });
                await using var reader = await insert.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await reader.ReadAsync(ct).ConfigureAwait(false);
                seq = reader.GetInt64(0);
                receivedAt = reader.GetFieldValue<DateTimeOffset>(1);
            }

            // sql.md 8.2 step 4: bump event.next_seq.
            await using (var bump = new NpgsqlCommand(
                "update event set next_seq = next_seq + 1, updated_at = now() where id = $1;", conn, tx))
            {
                bump.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                await bump.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // sql.md 8.2 step 5: stamp beacon.
            await using (var stamp = new NpgsqlCommand(@"
update beacon set last_seen_at = now(), last_location_at = now(), stale_since = null, updated_at = now()
where id = $1;", conn, tx))
            {
                stamp.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                await stamp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // sql.md 8.2 step 6: read the snapshot row so the live object built after
            // commit carries the exact snapshotUrl the transaction saw.
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

        // api.md 16: log `location stored` at Information with seq, beaconId,
        // eventId, published. CloudWatch's `LocationPublished` metric filter
        // keys on this line.
        _counters.IncrementLocationStored();
        if (published) _counters.IncrementLocationPublished();
        _logger.LogInformation("location stored seq={Seq} beaconId={BeaconId} eventId={EventId} published={Published}",
            seq, beaconId, eventId, published);

        // After commit: on the ingest path, fire the CDN write (contracts 1.8).
        if (published)
        {
            var fields = new LocationSourceFields(
                EventId: eventId,
                EventStatusId: statusId,
                SnapshotVersion: snapshotVersion,
                SnapshotUrl: snapshotUrl,
                Seq: seq,
                Lat: body.Lat,
                Lng: body.Lng,
                SpeedMps: body.SpeedMps,
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
}
