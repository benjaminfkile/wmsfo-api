using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: one preview per resource, plus `ApplyAsync` that runs the same
// statements as the delete inside the delete's transaction. The events preview
// answers `blocked` for the live and current event; deletes cascade locations,
// messages, cookies, status history, and pending outbox rows.
public static class EventImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        short status = 0;
        bool isCurrent = false;
        bool exists = false;
        await using (var read = new NpgsqlCommand(
            "select status_id, is_current from event where id = $1;", conn, tx))
        {
            read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                exists = true;
                status = reader.GetInt16(0);
                isCurrent = reader.GetBoolean(1);
            }
        }
        if (!exists) return impact;

        if (status == 3) impact.Blocked = ImpactHelpers.LiveEventBlocked;
        else if (isCurrent) impact.Blocked = ImpactHelpers.CurrentEventBlocked;

        var parameters = ImpactHelpers.LongId(id);

        // locations (no meaningful name, use seq as a text label).
        var locations = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "location", "location", "event_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (locations is not null) impact.Deletes.Add(locations);

        // event_message: body is the closest to a name.
        var messages = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "event_message", "event_message", "body", "event_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (messages is not null) impact.Deletes.Add(messages);

        // event_status_history: no name column, count only.
        var history = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "event_status_history", "event_status_history", "event_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (history is not null) impact.Deletes.Add(history);

        // cookies: names are the person emails.
        int cookieCount = 0;
        await using (var cmd = new NpgsqlCommand(
            "select count(*)::int from cookie where event_id = $1;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            cookieCount = r is int i ? i : Convert.ToInt32(r);
        }
        if (cookieCount > 0)
        {
            var cookieNames = new List<string>();
            await using (var cmd = new NpgsqlCommand(@"
select coalesce(p.email, '') from cookie c
left join person p on p.id = c.person_id
where c.event_id = $1
order by c.id limit 10;", conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    cookieNames.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            }
            impact.Deletes.Add(new ImpactGroupDto
            {
                Entity = "cookie",
                Count = cookieCount,
                Names = cookieNames,
            });
        }

        // pending outbox rows for this event (topic-based).
        var outboxRows = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "outbox", "outbox",
            "published_at is null and topic in ('event.status_changed', 'event.message_posted') " +
            "and (payload->>'eventId')::bigint = $1",
            parameters, ct).ConfigureAwait(false);
        if (outboxRows is not null) impact.Deletes.Add(outboxRows);

        return impact;
    }

    // ApplyAsync runs the statements the FKs and the endpoint depend on. With
    // cascades set on location, cookie, event_message, and event_status_history,
    // only the pending outbox rows need an explicit delete.
    public static async Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
    {
        await using (var del = new NpgsqlCommand(@"
delete from outbox
where published_at is null
  and topic in ('event.status_changed', 'event.message_posted')
  and (payload->>'eventId')::bigint = $1;", conn, tx))
        {
            del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
