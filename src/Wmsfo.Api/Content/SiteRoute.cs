using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Content;

// The site route (contracts 1.3a `settings.route`): `{ routeId, mapConfig? }`
// in the site settings. The published one is the newest content version's;
// the draft one is the site settings draft's. An absent `route` or a null
// `routeId` means no route; an absent or null `mapConfig` means every
// built-in default.
public static class SiteRoute
{
    // The newest content version's `settings.route`, or null when there is no
    // version or it carries no route.
    public const string PublishedSql = @"
select document->'settings'->'route'
from content_version order by id desc limit 1;";

    // The site settings draft's `route`, or null when the draft carries none.
    public const string DraftSql = @"
select data->'route' from site_setting_draft where id = 1;";

    public static Task<JsonNode?> ReadPublishedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct) =>
        ReadAsync(PublishedSql, conn, tx, ct);

    public static Task<JsonNode?> ReadDraftAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct) =>
        ReadAsync(DraftSql, conn, tx, ct);

    private static async Task<JsonNode?> ReadAsync(string sql, NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return r is string json ? JsonNode.Parse(json) : null;
    }

    // The `routeId` of a `route` value when it is an integer; null when the
    // value is absent, not an object, or its `routeId` is null or not an
    // integer.
    public static long? RouteId(JsonNode? route)
    {
        if (route is not JsonObject obj) return null;
        if (obj["routeId"] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return null;
        if (value.TryGetValue<long>(out var id)) return id;
        if (value.TryGetValue<double>(out var d) && d == Math.Floor(d) && d >= long.MinValue && d <= long.MaxValue)
            return (long)d;
        return null;
    }

    // The `mapConfig` of a `route` value as the typed configuration; null when
    // it is absent, null, or not a RouteMapConfig.
    public static RouteMapConfig? MapConfig(JsonNode? route)
    {
        if (route is not JsonObject obj || obj["mapConfig"] is not JsonObject config) return null;
        try
        {
            return config.Deserialize<RouteMapConfig>(CanonicalJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
