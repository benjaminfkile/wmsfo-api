using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wmsfo.Api.Data;
using Wmsfo.Api.Data.Migrations;

namespace Wmsfo.Api.IntegrationTests;

// The A83GlobalLandmarks migration (sql.md 14) folds every event's
// route_map_config landmarks into the site settings draft (the current event
// first, then by year descending, duplicates dropped, at most 50) and removes
// the key from every event; a config left empty becomes null. A second run
// changes nothing.
public sealed class A83GlobalLandmarksMigrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public A83GlobalLandmarksMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migration_folds_event_landmarks_into_the_settings_draft_once()
    {
        await MigrateToAsync("A82MessageNotify");
        await RunSqlAsync(@"
delete from location;
delete from cookie;
delete from event_status_history;
delete from event_message;
delete from event;
update site_setting_draft set data = '{""siteName"":""Test site""}'::jsonb, updated_at = now() - interval '1 day' where id = 1;
insert into event (year, name, status_id, is_current, created_by, updated_at, route_map_config)
values (2025, 'Older', 1, false, 'seed', now(),
        '{""landmarks"":[{""name"":""caras PARK"",""lat"":46.870301,""lng"":-113.995799},{""name"":""Airport"",""lat"":46.92,""lng"":-114.09}]}'::jsonb),
       (2026, 'Current', 1, true, 'seed', now(),
        '{""display"":{""arrows"":false},""landmarks"":[{""name"":""Caras Park"",""lat"":46.8703,""lng"":-113.9958,""icon"":{""source"":""library"",""id"":""tree""}},{""name"":""Fort Missoula"",""lat"":46.8455,""lng"":-114.0569},{""name"":""Courthouse"",""lat"":46.87,""lng"":-113.99,""description"":""The tree lighting.""}]}'::jsonb);");

        await MigrateToAsync(null);

        var (draft, updatedAt) = await ReadDraftAsync();
        Assert.Equal("Test site", draft.RootElement.GetProperty("siteName").GetString());
        var landmarks = draft.RootElement.GetProperty("landmarks");
        Assert.Equal(
            new[] { "Caras Park", "Fort Missoula", "Courthouse", "Airport" },
            landmarks.EnumerateArray().Select(l => l.GetProperty("name").GetString()).ToArray());
        Assert.Equal("tree", landmarks[0].GetProperty("icon").GetProperty("id").GetString());
        Assert.Equal("The tree lighting.", landmarks[2].GetProperty("description").GetString());

        var configs = await ReadConfigsAsync();
        Assert.Equal("{\"display\": {\"arrows\": false}}", configs[2026]);
        Assert.Null(configs[2025]);

        // A second run changes nothing more.
        await RunSqlAsync(A83GlobalLandmarks.FoldLandmarksSql + A83GlobalLandmarks.StripEventLandmarksSql);
        var (again, updatedAgain) = await ReadDraftAsync();
        Assert.Equal(draft.RootElement.GetRawText(), again.RootElement.GetRawText());
        Assert.Equal(updatedAt, updatedAgain);
        Assert.Equal(configs, await ReadConfigsAsync());
    }

    [Fact]
    public async Task Fold_keeps_at_most_fifty_and_writes_nothing_without_event_landmarks()
    {
        await MigrateToAsync(null);
        var many = string.Join(",", Enumerable.Range(0, 60)
            .Select(i => $"{{\"name\":\"L{i}\",\"lat\":46.{i:D2},\"lng\":-114}}"));
        await RunSqlAsync($@"
delete from location;
delete from cookie;
delete from event_status_history;
delete from event_message;
delete from event;
update site_setting_draft set data = '{{""siteName"":""Test site""}}'::jsonb where id = 1;
insert into event (year, name, status_id, is_current, created_by, updated_at, route_map_config)
values (2027, 'Many', 1, false, 'seed', now(), '{{""landmarks"":[{many}]}}'::jsonb),
       (2028, 'None', 1, false, 'seed', now(), '{{""landmarks"":[]}}'::jsonb);");

        await RunSqlAsync(A83GlobalLandmarks.FoldLandmarksSql + A83GlobalLandmarks.StripEventLandmarksSql);
        var (draft, _) = await ReadDraftAsync();
        var landmarks = draft.RootElement.GetProperty("landmarks");
        Assert.Equal(50, landmarks.GetArrayLength());
        Assert.Equal("L0", landmarks[0].GetProperty("name").GetString());
        Assert.Equal("L49", landmarks[49].GetProperty("name").GetString());
        Assert.All((await ReadConfigsAsync()).Values, Assert.Null);

        await RunSqlAsync(@"
update site_setting_draft set data = '{""siteName"":""Test site""}'::jsonb where id = 1;
update event set route_map_config = '{""landmarks"":[]}'::jsonb;");
        await RunSqlAsync(A83GlobalLandmarks.FoldLandmarksSql + A83GlobalLandmarks.StripEventLandmarksSql);
        var (untouched, _) = await ReadDraftAsync();
        Assert.False(untouched.RootElement.TryGetProperty("landmarks", out _));
    }

    // Migrates the fixture database to the named migration, or to the newest
    // when null, as the migrate role that owns the tables.
    private async Task MigrateToAsync(string? target)
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

    private async Task<Dictionary<int, string?>> ReadConfigsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select year, route_map_config::text from event;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var configs = new Dictionary<int, string?>();
        while (await reader.ReadAsync())
            configs[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return configs;
    }

    private async Task RunSqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
