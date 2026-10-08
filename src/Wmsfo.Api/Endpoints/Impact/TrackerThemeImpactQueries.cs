using System.Globalization;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 11a.10 "Impact and delete". The preview unlinks the events that
// enable the theme, warns per default flag the renderer loses, and blocks a
// google theme that is the only enabled Google theme on some event (the first
// such event by year desc). ApplyAsync re-enables a replacement on every
// referencing event and moves each default flag to it through the clear-then-
// set pair of sql.md 4.2, then deletes the row (the join rows cascade).
public static class TrackerThemeImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var events = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "event", "event", "name",
            "id in (select event_id from event_tracker_theme where theme_id = $1)",
            ImpactHelpers.LongId(id), ct).ConfigureAwait(false);
        if (events is not null) impact.Unlinks.Add(events);

        string renderer;
        bool light, dark;
        await using (var cmd = new NpgsqlCommand(
            "select renderer, default_light_mode, default_dark_mode from tracker_theme where id = $1;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return impact;
            renderer = reader.GetString(0);
            light = reader.GetBoolean(1);
            dark = reader.GetBoolean(2);
        }
        if (light)
            impact.Warnings.Add(string.Format(CultureInfo.InvariantCulture, ImpactHelpers.ThemeDefaultWarning, renderer, "light"));
        if (dark)
            impact.Warnings.Add(string.Format(CultureInfo.InvariantCulture, ImpactHelpers.ThemeDefaultWarning, renderer, "dark"));

        if (renderer == "google")
        {
            await using var cmd = new NpgsqlCommand(@"
select e.name from event e
where exists (select 1 from event_tracker_theme et where et.event_id = e.id and et.theme_id = $1)
  and not exists (
    select 1 from event_tracker_theme et2 join tracker_theme t on t.id = et2.theme_id
    where et2.event_id = e.id and t.renderer = 'google' and t.id <> $1)
order by e.year desc limit 1;", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string name)
                impact.Blocked = string.Format(CultureInfo.InvariantCulture, ImpactHelpers.LastGoogleThemeBlocked, name);
        }
        return impact;
    }

    // A replacement must exist (404) and be another theme of the same
    // renderer (400 validation_failed at replacementId).
    public static async Task RequireReplacementAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, long replacementId, CancellationToken ct)
    {
        if (replacementId == id)
            RequestValidation.Throw("replacementId", "must name another theme");
        await using var cmd = new NpgsqlCommand(@"
select r.renderer = t.renderer
from tracker_theme r, tracker_theme t
where r.id = $1 and t.id = $2;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = replacementId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        var same = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (same is not bool b)
            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "replacement theme not found");
        if (!b)
            RequestValidation.Throw("replacementId", "must name a theme of the same renderer");
    }

    public static async Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, long? replacementId, string actor, CancellationToken ct)
    {
        if (replacementId is long r)
        {
            await RequireReplacementAsync(conn, tx, id, r, ct).ConfigureAwait(false);
            await using (var cmd = new NpgsqlCommand(@"
insert into event_tracker_theme (event_id, theme_id)
select event_id, $2 from event_tracker_theme where theme_id = $1
on conflict do nothing;", conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = r });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            bool light, dark;
            await using (var cmd = new NpgsqlCommand(
                "select default_light_mode, default_dark_mode from tracker_theme where id = $1;", conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await reader.ReadAsync(ct).ConfigureAwait(false);
                light = reader.GetBoolean(0);
                dark = reader.GetBoolean(1);
            }
            if (light) await MoveFlagAsync(conn, tx, "default_light_mode", r, actor, ct).ConfigureAwait(false);
            if (dark) await MoveFlagAsync(conn, tx, "default_dark_mode", r, actor, ct).ConfigureAwait(false);
        }

        await using var del = new NpgsqlCommand("delete from tracker_theme where id = $1;", conn, tx);
        del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // sql.md 4.2: clear the flag on the renderer's holder other than target,
    // then set it on target; two statements so the partial unique index is
    // checked after each.
    public static async Task MoveFlagAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string column, long target, string actor, CancellationToken ct)
    {
        await using (var clear = new NpgsqlCommand($@"
update tracker_theme set {column} = false, updated_by = $2, updated_at = now()
where renderer = (select renderer from tracker_theme where id = $1) and {column} and id <> $1;", conn, tx))
        {
            clear.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = target });
            clear.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = actor });
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using var set = new NpgsqlCommand($@"
update tracker_theme set {column} = true, updated_by = $2, updated_at = now() where id = $1;", conn, tx);
        set.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = target });
        set.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = actor });
        await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
