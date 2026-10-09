using System.Globalization;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 11.6 "Patch, impact, delete". The preview unlinks the events whose
// map it is and warns that their viewers get Google Maps, naming the current
// event when it is among them and saying so when that event is live; nothing
// blocks a map delete. ApplyAsync repoints those events to a replacement
// (ready, another row, its package containing every event's box) or nulls
// their map, then deletes the row.
public static class TrackerMapImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var events = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "event", "event", "name", "tracker_map_id = $1",
            ImpactHelpers.LongId(id), ct).ConfigureAwait(false);
        if (events is null) return impact;
        impact.Unlinks.Add(events);
        impact.Warnings.Add(string.Format(CultureInfo.InvariantCulture, ImpactHelpers.MapEventsWarning, events.Count));

        await using var cmd = new NpgsqlCommand(
            "select name, status_id from event where tracker_map_id = $1 and is_current;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            impact.Warnings.Add(string.Format(CultureInfo.InvariantCulture, ImpactHelpers.MapCurrentEventWarning, name));
            if (reader.GetInt16(1) == 3)
                impact.Warnings.Add(string.Format(CultureInfo.InvariantCulture, ImpactHelpers.MapLiveEventWarning, name));
        }
        return impact;
    }

    // A replacement must exist (404) and be another ready map (400
    // validation_failed at replacementId).
    public static async Task RequireReplacementAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, long replacementId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select state from tracker_map where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = replacementId });
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string state)
            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "replacement map not found");
        if (replacementId == id)
            RequestValidation.Throw("replacementId", "must name another map");
        if (state != "ready")
            RequestValidation.Throw("replacementId", "must name a ready map");
    }

    public static async Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, long? replacementId, CancellationToken ct)
    {
        if (replacementId is long r)
        {
            await RequireReplacementAsync(conn, tx, id, r, ct).ConfigureAwait(false);
            var package = await EventTrackerRules.MapBboxAsync(conn, tx, r, ct).ConfigureAwait(false)
                ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "replacement map not found");
            await using var cmd = new NpgsqlCommand(
                "select id, tracker_bbox::text from event where tracker_map_id = $1 order by id;", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var eventId = reader.GetInt64(0);
                if (!EventTrackerRules.Contains(package, EventTrackerRules.ParseStored(reader.GetString(1))))
                    throw ReplacementMisses(eventId);
            }
        }

        await using (var update = new NpgsqlCommand(
            "update event set tracker_map_id = $2, updated_at = now() where tracker_map_id = $1;", conn, tx))
        {
            update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)replacementId ?? DBNull.Value });
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var del = new NpgsqlCommand("delete from tracker_map where id = $1;", conn, tx);
        del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static ApiException ReplacementMisses(long eventId)
    {
        const string message = "the replacement's package does not contain the box of an event that uses the map";
        return new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
            "replacementId " + message,
            new ReplacementMissDetails(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["replacementId"] = message }, eventId));
    }
}

// details of 400 validation_failed at replacementId on a map delete whose
// replacement misses an event's box: the fields map and the first such event.
public sealed record ReplacementMissDetails(IReadOnlyDictionary<string, string> Fields, long EventId);
