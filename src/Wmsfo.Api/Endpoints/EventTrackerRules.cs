using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.Endpoints;

// The event's tracker fields (contracts 4.5 Events; sql.md 8.4b, 8.5, 8.7):
// the box check, the containment rule, the create rule for what the body does
// not give, and the theme set writes. Every statement runs inside the caller's
// transaction.
public static class EventTrackerRules
{
    // The tracker fields the create rule settles for a new event:
    // the map, and either the event whose theme set it copies or every theme.
    public sealed record Inherited(long? MapId, long? ThemesFromEventId, bool AllThemes);

    // A written box: `$defs/Bbox` and the side limits of BboxRules, reported
    // at `field`; null after a problem.
    public static Bbox? ReadBbox(JsonElement value, string field, SchemaValidator validator, RequestValidation v)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            v.Field(field, "must be a Bbox object");
            return null;
        }
        var problems = validator.ValidateBbox(JsonNode.Parse(value.GetRawText()));
        if (problems.Count > 0)
        {
            v.Field(field, problems[0].Message);
            return null;
        }
        return JsonSerializer.Deserialize<Bbox>(value.GetRawText(), CanonicalJson.Options)!;
    }

    // `outer` contains `inner` on the JSON numbers.
    public static bool Contains(Bbox outer, Bbox inner) =>
        outer.West <= inner.West && outer.South <= inner.South
        && outer.East >= inner.East && outer.North >= inner.North;

    public static Bbox ParseStored(string json) =>
        JsonSerializer.Deserialize<Bbox>(json, CanonicalJson.Options)!;

    public static string Serialize(Bbox box) =>
        JsonSerializer.Serialize(box, CanonicalJson.Options);

    public static Bbox Valley => ParseStored(TrackerThemeSeed.ValleyBboxJson);

    // The box a new event takes when the body names none: the newest content
    // version's settings.tracker.defaultBbox, or the Missoula valley constant
    // when that document has no `tracker` key.
    public static async Task<Bbox> DefaultBboxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select document->'settings'->'tracker'->'defaultBbox'
from content_version order by id desc limit 1;", conn, tx);
        var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return r is string json ? ParseStored(json) : Valley;
    }

    // sql.md 8.7: the map and the theme set come from the event with the
    // greatest year, the map only when it is ready and its package contains
    // `box`; with no event at all, every theme and the ready `basemap` row
    // when it contains `box`.
    public static async Task<Inherited> InheritAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Bbox box, CancellationToken ct)
    {
        long? fromId = null;
        long? candidateMap = null;
        await using (var cmd = new NpgsqlCommand(
            "select id, tracker_map_id from event order by year desc limit 1;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                fromId = reader.GetInt64(0);
                candidateMap = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
        }

        if (fromId is not null)
        {
            long? mapId = null;
            if (candidateMap is long m)
            {
                await using var cmd = new NpgsqlCommand(
                    "select bbox::text from tracker_map where id = $1 and state = 'ready' for share;", conn, tx);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = m });
                if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string bbox && Contains(ParseStored(bbox), box))
                    mapId = m;
            }
            return new Inherited(mapId, fromId, AllThemes: false);
        }

        long? valley = null;
        await using (var cmd = new NpgsqlCommand(
            "select id, bbox::text from tracker_map where prefix = $1 and state = 'ready' for share;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = TrackerThemeSeed.ValleyPrefix });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false) && Contains(ParseStored(reader.GetString(1)), box))
                valley = reader.GetInt64(0);
        }
        return new Inherited(valley, null, AllThemes: true);
    }

    // The new event's join rows: a copy of `fromEventId`'s, or every theme.
    public static async Task CopyThemesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long newId, long? fromEventId, bool allThemes, CancellationToken ct)
    {
        if (!allThemes && fromEventId is null) return;
        await using var cmd = new NpgsqlCommand(allThemes
            ? "insert into event_tracker_theme (event_id, theme_id) select $1, id from tracker_theme on conflict do nothing;"
            : "insert into event_tracker_theme (event_id, theme_id) select $1, theme_id from event_tracker_theme where event_id = $2 on conflict do nothing;",
            conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = newId });
        if (!allThemes)
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = fromEventId!.Value });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // A map an event may take: known (404 otherwise), ready, and its package
    // containing `box` (400 validation_failed at trackerMapId otherwise).
    public static async Task CheckMapAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long mapId, Bbox box, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select state, bbox::text from tracker_map where id = $1 for share;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = mapId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "map not found");
        if (reader.GetString(0) != "ready")
            RequestValidation.Throw("trackerMapId", "must name a ready map");
        if (!Contains(ParseStored(reader.GetString(1)), box))
            RequestValidation.Throw("trackerMapId", "the map's package must contain the event's trackerBbox");
    }

    // The one sentence every write answers at trackerBbox when the box leaves
    // the event's map.
    public const string InsideMapMessage = "must lie inside the map's area";

    // The one sentence every write answers at trackerStartView when the view's
    // centre lies outside the box that applies after the write.
    public const string InsideBboxMessage = "must lie inside trackerBbox";

    // A written trackerStartView: absent (Set false), null (Set true, no
    // view), or a `$defs/TrackerStartView` object; any other value is a
    // problem at trackerStartView. The box rule is checked once the box after
    // the write is known.
    public static (bool Set, TrackerStartView? View) ReadStartView(
        JsonElement value, SchemaValidator validator, RequestValidation v)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
                return (false, null);
            case JsonValueKind.Null:
                return (true, null);
            case JsonValueKind.Object:
                var problems = validator.ValidateTrackerStartView(JsonNode.Parse(value.GetRawText()));
                if (problems.Count > 0)
                {
                    v.Field("trackerStartView", problems[0].Message);
                    return (false, null);
                }
                return (true, JsonSerializer.Deserialize<TrackerStartView>(value.GetRawText(), CanonicalJson.Options)!);
            default:
                v.Field("trackerStartView", "must be a TrackerStartView object or null");
                return (false, null);
        }
    }

    // The view's centre lies inside `box` (west <= lng <= east, south <= lat
    // <= north).
    public static bool Contains(Bbox box, TrackerStartView view) =>
        double.IsFinite(view.Center.Lat) && double.IsFinite(view.Center.Lng)
        && box.West <= view.Center.Lng && view.Center.Lng <= box.East
        && box.South <= view.Center.Lat && view.Center.Lat <= box.North;

    // The start view a write stores: the named one, which must lie inside
    // `box` (400 validation_failed at trackerStartView otherwise); else the
    // kept one while its centre stays inside `box`, else null.
    public static TrackerStartView? SettleStartView(
        (bool Set, TrackerStartView? View) named, TrackerStartView? kept, Bbox box)
    {
        if (named.Set)
        {
            if (named.View is not null && !Contains(box, named.View))
                RequestValidation.Throw("trackerStartView", InsideBboxMessage);
            return named.View;
        }
        return kept is not null && Contains(box, kept) ? kept : null;
    }

    public static TrackerStartView? ParseStoredStartView(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<TrackerStartView>(json, CanonicalJson.Options);

    public static string Serialize(TrackerStartView view) =>
        JsonSerializer.Serialize(view, CanonicalJson.Options);

    // A written trackerMapId on create or clone: absent (Set false), null
    // (Set true, no id), or a map id; any other value is a problem at
    // trackerMapId.
    public static (bool Set, long? Id) ReadMapId(JsonElement value, RequestValidation v)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
                return (false, null);
            case JsonValueKind.Null:
                return (true, null);
            case JsonValueKind.Number when value.TryGetInt64(out var id):
                return (true, id);
            default:
                v.Field("trackerMapId", "must be a map id or null");
                return (false, null);
        }
    }

    // The area of a map a create or clone names: known (404 otherwise) and
    // ready (400 validation_failed at trackerMapId otherwise).
    public static async Task<Bbox> ReadyMapBboxAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long mapId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select state, bbox::text from tracker_map where id = $1 for share;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = mapId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "map not found");
        if (reader.GetString(0) != "ready")
            RequestValidation.Throw("trackerMapId", "must name a ready map");
        return ParseStored(reader.GetString(1));
    }

    // The package box of a map, or null when the row is gone.
    public static async Task<Bbox?> MapBboxAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long mapId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select bbox::text from tracker_map where id = $1 for share;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = mapId });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string json ? ParseStored(json) : null;
    }

    // Replaces the event's enabled set whole: every id known (404), at least
    // one google theme (400 validation_failed at trackerThemeIds), then one
    // delete and one multi-row insert. The ids are distinct (checked by the
    // caller).
    public static async Task ReplaceThemesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long eventId, long[] themeIds, CancellationToken ct)
    {
        var found = 0;
        var google = false;
        await using (var cmd = new NpgsqlCommand(
            "select renderer from tracker_theme where id = any($1) for share;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = themeIds });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                found++;
                if (reader.GetString(0) == "google") google = true;
            }
        }
        if (found != themeIds.Length)
            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "theme not found");
        if (!google)
            RequestValidation.Throw("trackerThemeIds", "must include at least one google theme");

        await using (var del = new NpgsqlCommand("delete from event_tracker_theme where event_id = $1;", conn, tx))
        {
            del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var ins = new NpgsqlCommand(
            "insert into event_tracker_theme (event_id, theme_id) select $1, unnest($2::bigint[]);", conn, tx))
        {
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = themeIds });
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
