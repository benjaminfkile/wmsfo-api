using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wmsfo.Api.Data;
using Wmsfo.Api.Data.Migrations;

namespace Wmsfo.Api.IntegrationTests;

// The A88GlobalPlaces migration (sql.md 14) builds the site settings draft's
// `places` from the first map section that filters places and from the
// current (else newest) event's route map `pois`, removes `poiFilter` and
// `poiKinds` from every map section and `pois` from every event (a config
// left empty becomes null), and leaves the draft alone when nothing carries a
// choice. A second run changes nothing.
public sealed class A88GlobalPlacesMigrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public A88GlobalPlacesMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migration_folds_the_map_section_and_event_places_into_the_settings_draft_once()
    {
        await MigrateToAsync("A84RoutePreviewMapOnly");
        await RunSqlAsync(SeedSql(
            "'{\"themes\":[\"night\"],\"poiFilter\":false}'",
            "'{\"themes\":[\"standard\"],\"poiFilter\":true,\"poiKinds\":[\"park\",\"school\"]}'",
            "'{\"pois\":{\"kinds\":[\"hospital\"]}}'",
            "'{\"display\":{\"arrows\":false},\"pois\":{\"kinds\":[\"park\"]}}'"));

        await MigrateToAsync(null);

        var (draft, updatedAt) = await ReadDraftAsync();
        Assert.Equal("Test site", draft.RootElement.GetProperty("siteName").GetString());
        var places = draft.RootElement.GetProperty("places");
        Assert.Equal(new[] { "routeMap", "tracker" },
            places.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "park", "school" }, Kinds(places.GetProperty("tracker")));
        Assert.Equal(new[] { "park" }, Kinds(places.GetProperty("routeMap")));

        var sections = await ReadMapSectionsAsync();
        Assert.Equal(2, sections.Count);
        Assert.All(sections.Values, data =>
        {
            Assert.DoesNotContain("poiFilter", data, StringComparison.Ordinal);
            Assert.DoesNotContain("poiKinds", data, StringComparison.Ordinal);
        });
        Assert.Equal("{\"themes\": [\"night\"]}", sections[0]);
        Assert.Equal("{\"themes\": [\"standard\"]}", sections[1]);

        var configs = await ReadConfigsAsync();
        Assert.Equal("{\"display\": {\"arrows\": false}}", configs[2026]);
        Assert.Null(configs[2025]);

        // A second run changes nothing more.
        await RunSqlAsync(A88GlobalPlaces.FoldPlacesSql + A88GlobalPlaces.StripMapPoiKeysSql + A88GlobalPlaces.StripEventPoisSql);
        var (again, updatedAgain) = await ReadDraftAsync();
        Assert.Equal(draft.RootElement.GetRawText(), again.RootElement.GetRawText());
        Assert.Equal(updatedAt, updatedAgain);
        Assert.Equal(sections, await ReadMapSectionsAsync());
        Assert.Equal(configs, await ReadConfigsAsync());
    }

    [Fact]
    public async Task Fold_leaves_the_draft_untouched_when_nothing_carries_a_choice()
    {
        await MigrateToAsync(null);
        await RunSqlAsync(SeedSql(
            "'{\"themes\":[\"night\"],\"poiFilter\":false}'",
            "'{\"themes\":[\"standard\"],\"poiKinds\":[\"park\"]}'",
            "'{\"display\":{\"arrows\":true}}'",
            "null"));
        var (before, updatedBefore) = await ReadDraftAsync();

        await RunSqlAsync(A88GlobalPlaces.FoldPlacesSql + A88GlobalPlaces.StripMapPoiKeysSql + A88GlobalPlaces.StripEventPoisSql);

        var (after, updatedAfter) = await ReadDraftAsync();
        Assert.Equal(before.RootElement.GetRawText(), after.RootElement.GetRawText());
        Assert.False(after.RootElement.TryGetProperty("places", out _));
        Assert.Equal(updatedBefore, updatedAfter);
        Assert.Equal("{\"themes\": [\"standard\"]}", (await ReadMapSectionsAsync())[1]);
    }

    private static string[] Kinds(JsonElement part) =>
        part.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()!).ToArray();

    // A settings draft without `places` written a day ago, a page with
    // the two map sections, and the 2025 and current 2026 events with the
    // given route map configs (SQL literals).
    private static string SeedSql(string firstMap, string secondMap, string olderConfig, string currentConfig) => $@"
delete from section_item;
delete from section;
delete from page where slug = 'a88-live';
delete from location;
delete from cookie;
delete from event_status_history;
delete from event_message;
delete from event;
update site_setting_draft set data = '{{""siteName"":""Test site""}}'::jsonb, updated_at = now() - interval '1 day' where id = 1;
with p as (
  insert into page (slug, title, nav_label, nav_position, is_hidden, role, created_by, updated_by)
  values ('a88-live', 'Live', null, 90, false, 'none', 'seed', 'seed') returning id
)
insert into section (page_id, kind, position, is_hidden, data, presentation, updated_by)
select p.id, v.kind, v.position, false, v.data::jsonb, '{{}}'::jsonb, 'seed'
from p, (values
  ('map', 0, {firstMap}),
  ('map', 1, {secondMap})
) as v(kind, position, data);
insert into event (year, name, status_id, is_current, created_by, updated_at, route_map_config)
values (2025, 'Older', 1, false, 'seed', now(), {olderConfig}::jsonb),
       (2026, 'Current', 1, true, 'seed', now(), {currentConfig}::jsonb);";

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

    // The test page's map section data as text, keyed by position.
    private async Task<Dictionary<int, string>> ReadMapSectionsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
select s.position, s.data::text from section s
join page p on p.id = s.page_id
where p.slug = 'a88-live' and s.kind = 'map';", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var sections = new Dictionary<int, string>();
        while (await reader.ReadAsync())
            sections[reader.GetInt32(0)] = reader.GetString(1);
        return sections;
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
