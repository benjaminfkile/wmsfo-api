using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: cookie type deletes take its cookies with it; when the current
// event is live and any cookie of this type belongs to it, we warn "An event
// is live; its cookie tally drops by <n>." (the sentence in 5b).
public static class CookieTypeImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var parameters = ImpactHelpers.LongId(id);

        var cookies = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "cookie", "cookie", "cookie_type_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (cookies is not null) impact.Deletes.Add(cookies);

        // Live-event warning: how many of the deleted cookies belong to the
        // live event.
        int liveCount = 0;
        await using (var cmd = new NpgsqlCommand(@"
select count(*)::int from cookie c
join event e on e.id = c.event_id
where c.cookie_type_id = $1 and e.status_id = 3;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            liveCount = r is int i ? i : Convert.ToInt32(r);
        }
        if (liveCount > 0)
        {
            impact.Warnings.Add(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                ImpactHelpers.CookieTallyWarning, liveCount));
        }
        return impact;
    }

    public static Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
        => Task.CompletedTask;
}
