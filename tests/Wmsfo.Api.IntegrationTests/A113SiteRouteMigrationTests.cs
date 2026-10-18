using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wmsfo.Api.Data;

namespace Wmsfo.Api.IntegrationTests;

// The A113SiteRoute migration (sql.md 14) folds into the site settings draft,
// when the draft has no `route`, `route.routeId` from the current event's
// route_id, else the newest event's that has one, else null, and
// `route.mapConfig` from the current event's route_map_config, else the
// newest event's non-null one, else nothing; then it drops event.route_id,
// event.route_map_config, and poster.route_id. A draft that already has
// `route` is left alone.
public sealed class A113SiteRouteMigrationTests : IClassFixture<PostgresFixture>
{
    private const string BeforeA113 = "A111BeaconState";
    private const string A113 = "A113SiteRoute";

    private readonly PostgresFixture _fixture;

    public A113SiteRouteMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task The_current_events_route_and_config_win()
    {
        await MigrateToAsync(BeforeA113);
        await RunSqlAsync(ResetSql("'{\"siteName\":\"Test site\"}'"));
        var (a, b, c) = (await InsertRouteAsync("A"), await InsertRouteAsync("B"), await InsertRouteAsync("C"));
        await RunSqlAsync($@"
insert into event (year, name, status_id, is_current, created_by, updated_at, route_id, route_map_config)
values (2025, 'Older', 1, false, 'seed', now(), {a}, '{{""display"":{{""arrows"":false}}}}'::jsonb),
       (2026, 'Current', 1, true, 'seed', now(), {b}, '{{""controls"":{{""terrain"":true}}}}'::jsonb),
       (2027, 'Newest', 1, false, 'seed', now(), {c}, '{{""display"":{{""routeWidth"":""thick""}}}}'::jsonb);
insert into poster (name, route_id, created_by, updated_at) values ('Poster', {c}, 'seed', now());");

        await MigrateToAsync(A113);

        var (draft, _) = await ReadDraftAsync();
        Assert.Equal("Test site", draft.RootElement.GetProperty("siteName").GetString());
        var route = draft.RootElement.GetProperty("route");
        Assert.Equal(b, route.GetProperty("routeId").GetInt64());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"controls\":{\"terrain\":true}}"),
            JsonNode.Parse(route.GetProperty("mapConfig").GetRawText())));

        var columns = await ListColumnsAsync();
        Assert.DoesNotContain(("event", "route_id"), columns);
        Assert.DoesNotContain(("event", "route_map_config"), columns);
        Assert.DoesNotContain(("poster", "route_id"), columns);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from poster where name = 'Poster';"));
    }

    [Fact]
    public async Task Without_a_current_route_the_newest_event_that_has_one_wins()
    {
        await MigrateToAsync(BeforeA113);
        await RunSqlAsync(ResetSql("'{\"siteName\":\"Test site\"}'"));
        var (a, d) = (await InsertRouteAsync("A"), await InsertRouteAsync("D"));
        await RunSqlAsync($@"
insert into event (year, name, status_id, is_current, created_by, updated_at, route_id, route_map_config)
values (2024, 'Oldest', 1, false, 'seed', now(), {d}, '{{""controls"":{{""fullscreen"":false}}}}'::jsonb),
       (2025, 'Older', 1, false, 'seed', now(), {a}, '{{""display"":{{""arrows"":false}}}}'::jsonb),
       (2026, 'Current', 1, true, 'seed', now(), null, null),
       (2027, 'Newest', 1, false, 'seed', now(), null, null);");

        await MigrateToAsync(A113);

        var route = (await ReadDraftAsync()).Data.RootElement.GetProperty("route");
        Assert.Equal(a, route.GetProperty("routeId").GetInt64());
        Assert.False(route.GetProperty("mapConfig").GetProperty("display").GetProperty("arrows").GetBoolean());
        Assert.Equal(new[] { "mapConfig", "routeId" },
            route.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task With_no_events_the_route_is_none_and_carries_no_config()
    {
        await MigrateToAsync(BeforeA113);
        await RunSqlAsync(ResetSql("'{\"siteName\":\"Test site\"}'"));

        await MigrateToAsync(A113);

        var (draft, updatedAt) = await ReadDraftAsync();
        var route = draft.RootElement.GetProperty("route");
        Assert.Equal(JsonValueKind.Null, route.GetProperty("routeId").ValueKind);
        Assert.False(route.TryGetProperty("mapConfig", out _));
        Assert.True(updatedAt > DateTimeOffset.UtcNow.AddHours(-1));
    }

    [Fact]
    public async Task A_draft_that_already_has_a_route_is_left_alone()
    {
        await MigrateToAsync(BeforeA113);
        await RunSqlAsync(ResetSql("'{\"siteName\":\"Test site\",\"route\":{\"routeId\":null}}'"));
        var a = await InsertRouteAsync("A");
        await RunSqlAsync($@"
insert into event (year, name, status_id, is_current, created_by, updated_at, route_id, route_map_config)
values (2026, 'Current', 1, true, 'seed', now(), {a}, '{{""display"":{{""arrows"":false}}}}'::jsonb);");
        var (before, updatedBefore) = await ReadDraftAsync();

        await MigrateToAsync(A113);

        var (after, updatedAfter) = await ReadDraftAsync();
        Assert.Equal(before.RootElement.GetRawText(), after.RootElement.GetRawText());
        Assert.Equal(updatedBefore, updatedAfter);
        Assert.Equal(JsonValueKind.Null, after.RootElement.GetProperty("route").GetProperty("routeId").ValueKind);
    }

    // No events, no routes, no posters, and the given settings draft written
    // a day ago.
    private static string ResetSql(string draft) => $@"
delete from location;
delete from cookie;
delete from event_status_history;
delete from event_message;
delete from event;
delete from poster;
delete from route;
update site_setting_draft set data = {draft}::jsonb, updated_at = now() - interval '1 day' where id = 1;";

    private async Task<long> InsertRouteAsync(string name)
    {
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
        return await ScalarAsync<long>($@"
insert into route (name, s3_key, url, sha256, point_count, uploaded_by)
values ('{name}', 'routes/{sha}.json', 'https://cdn.example/routes/{sha}.json', '{sha}', 2, 'seed')
returning id;");
    }

    // Migrates the fixture database to the named migration as the migrate
    // role that owns the tables.
    private async Task MigrateToAsync(string target)
    {
        var options = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.MigrateConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<(JsonDocument Data, DateTimeOffset? UpdatedAt)> ReadDraftAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select data::text, updated_at from site_setting_draft where id = 1;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (JsonDocument.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    private async Task<HashSet<(string Table, string Column)>> ListColumnsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select table_name, column_name from information_schema.columns where table_schema = 'public';", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var columns = new HashSet<(string, string)>();
        while (await reader.ReadAsync()) columns.Add((reader.GetString(0), reader.GetString(1)));
        return columns;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task RunSqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
