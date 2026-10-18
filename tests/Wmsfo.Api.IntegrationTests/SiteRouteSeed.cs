using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// Shared helper: names the site route (contracts 1.3a `settings.route`) in the
// published document or in the site settings draft without going through the
// content endpoints. A published route is a new content_version row (the
// starter content with the given settings, label `site-route`); the next
// [snapshot] write reads it as the newest version.
internal static class SiteRouteSeed
{
    public const string Label = "site-route";

    public static JsonObject Route(long? routeId, JsonNode? mapConfig = null)
    {
        var route = new JsonObject { ["routeId"] = routeId };
        if (mapConfig is not null) route["mapConfig"] = mapConfig;
        return route;
    }

    // A content version whose settings carry `route` (absent when null) and
    // `tracker` (absent when null).
    public static async Task PublishAsync(string connectionString, JsonNode? route, JsonNode? tracker = null)
    {
        var doc = JsonNode.Parse(CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildContentDocument()))!;
        var settings = doc["settings"]!.AsObject();
        if (route is not null) settings["route"] = route.DeepClone();
        if (tracker is not null) settings["tracker"] = tracker.DeepClone();
        var json = doc.ToJsonString();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into content_version (document, sha256, media_ids, label, published_by)
values ($1::jsonb, $2, '{}'::uuid[], $3, 'test');", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = CanonicalJson.Sha256Hex(Encoding.UTF8.GetBytes(json)) });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = Label });
        await cmd.ExecuteNonQueryAsync();
    }

    // Sets the site settings draft's `route`, or removes it when null.
    public static async Task SetDraftAsync(string connectionString, JsonNode? route)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = route is null
            ? new NpgsqlCommand("update site_setting_draft set data = data - 'route' where id = 1;", conn)
            : new NpgsqlCommand("update site_setting_draft set data = jsonb_set(data, '{route}', $1::jsonb) where id = 1;", conn);
        if (route is not null)
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = route.ToJsonString() });
        await cmd.ExecuteNonQueryAsync();
    }

    // Removes every version this helper published and the draft's route.
    public static async Task ClearAsync(NpgsqlConnection conn)
    {
        await using (var cmd = new NpgsqlCommand("delete from content_version where label = $1;", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = Label });
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand(
            "update site_setting_draft set data = data - 'route' where id = 1 and jsonb_typeof(data) = 'object';", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
