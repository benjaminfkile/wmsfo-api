using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: nothing references a route row by key. The site route is the
// route the site settings name (`settings.route.routeId`), in the draft or
// the newest published version; while either names it the delete is blocked
// (409 route_in_use) and the preview says so.
public static class RouteImpactQueries
{
    public const string SiteRouteBlocked = "This is the site route. Choose another route or None in Site settings first.";

    // Which site settings name the route: "draft" (the site settings draft)
    // and "published" (the newest content version), in that order; empty when
    // neither does.
    public static async Task<IReadOnlyList<string>> SiteRouteUsageAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select
  coalesce((select d.data->'route'->'routeId' = to_jsonb($1::bigint)
            from site_setting_draft d where d.id = 1), false),
  coalesce((select v.document->'settings'->'route'->'routeId' = to_jsonb($1::bigint)
            from content_version v order by v.id desc limit 1), false);", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var where = new List<string>();
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.GetBoolean(0)) where.Add("draft");
            if (reader.GetBoolean(1)) where.Add("published");
        }
        return where;
    }

    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        if ((await SiteRouteUsageAsync(conn, tx, id, ct).ConfigureAwait(false)).Count > 0)
            impact.Blocked = SiteRouteBlocked;
        return impact;
    }

    // Nothing references a route row; ApplyAsync is a no-op.
    public static Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
        => Task.CompletedTask;
}
