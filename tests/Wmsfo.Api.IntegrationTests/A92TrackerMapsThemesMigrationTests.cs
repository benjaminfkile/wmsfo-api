using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Wmsfo.Api.Data;
using Wmsfo.Api.Data.Migrations;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.IntegrationTests;

// The A92TrackerMapsThemes migration (sql.md 14.4) creates the four tracker
// tables and the two event columns, seeds the Missoula valley map, the eight
// themes, every join row, the state row, and the draft's tracker.defaultBbox,
// and removes `themes` and `defaultTheme` from every map section. A second
// run of the seeds changes nothing. Down drops everything, writes the six
// Google keys and `night` back into every map section, removes the draft's
// `tracker` key, and leaves the content versions as they were.
public sealed class A92TrackerMapsThemesMigrationTests : IClassFixture<PostgresFixture>
{
    private const string BeforeA92 = "A89HelpTopics";
    private static readonly string[] Tables = ["tracker_map", "tracker_theme", "event_tracker_theme", "tracker_theme_state"];
    private readonly PostgresFixture _fixture;

    public A92TrackerMapsThemesMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Up_seeds_once_and_down_restores_a_baseline_working_set()
    {
        await MigrateToAsync(BeforeA92);
        await RunSqlAsync(SeedSql);
        var versionsBefore = await ReadVersionsAsync();

        await MigrateToAsync(null);

        foreach (var table in Tables)
            Assert.False(string.IsNullOrEmpty(await ScalarAsync<string?>($"select obj_description('{table}'::regclass, 'pg_class');")));
        foreach (var column in new[] { "tracker_bbox", "tracker_map_id" })
            Assert.False(string.IsNullOrEmpty(await ScalarAsync<string?>(
                $"select col_description('event'::regclass, (select attnum from pg_attribute where attrelid = 'event'::regclass and attname = '{column}'));")));

        // The seeded map, on every event, with the valley box.
        var mapId = await ScalarAsync<long>(
            $"select id from tracker_map where prefix = 'basemap' and name = 'Missoula valley' and state = 'ready' and package_key = '{TrackerThemeSeed.ValleyPackageKey}' and min_zoom = 0 and max_zoom = 15 and terrain_max_zoom = 13;");
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from tracker_map;"));
        Assert.Equal(2L, await ScalarAsync<long>(
            $"select count(*) from event where tracker_map_id = {mapId} and tracker_bbox = '{TrackerThemeSeed.ValleyBboxJson}'::jsonb;"));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from event where tracker_map_id is null;"));

        // The eight themes as the seed table lists them.
        var themes = await ReadThemesAsync();
        Assert.Equal(TrackerThemeSeed.Themes.Count, themes.Count);
        foreach (var seed in TrackerThemeSeed.Themes)
        {
            var row = themes[(seed.Renderer, seed.Key)];
            Assert.Equal(seed.Name, row.Name);
            Assert.Equal(seed.SortOrder, row.SortOrder);
            Assert.Equal(seed.StyleSha256, row.StyleSha256);
            Assert.Equal(seed.StyleBytes, row.StyleBytes);
            Assert.Equal(seed.DefaultLightMode, row.DefaultLight);
            Assert.Equal(seed.DefaultDarkMode, row.DefaultDark);
            Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(seed.Chrome).RootElement, JsonDocument.Parse(row.Chrome).RootElement));
            Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(seed.Overlay).RootElement, JsonDocument.Parse(row.Overlay).RootElement));
        }
        foreach (var renderer in new[] { "google", "maplibre" })
        {
            Assert.Equal(1, themes.Values.Count(t => t.Renderer == renderer && t.DefaultLight));
            Assert.Equal(1, themes.Values.Count(t => t.Renderer == renderer && t.DefaultDark));
        }
        Assert.Equal(0L, await ScalarAsync<long>(
            "select count(*) from tracker_theme where created_by <> 'seed' or updated_by <> 'seed' or sprite_sha256 is not null or thumbnail_media_id is not null;"));

        // Every event enables all eight; the state row exists.
        Assert.Equal(16L, await ScalarAsync<long>("select count(*) from event_tracker_theme;"));
        Assert.Equal(0L, await ScalarAsync<long>(
            "select count(*) from event e cross join tracker_theme t where not exists (select 1 from event_tracker_theme et where et.event_id = e.id and et.theme_id = t.id);"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from tracker_theme_state where id = 1 and written_at is null;"));

        // The draft carries the default box; the map sections lose the two keys.
        var (draft, draftUpdatedAt) = await ReadDraftAsync();
        Assert.Equal("Test site", draft.RootElement.GetProperty("siteName").GetString());
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse(TrackerThemeSeed.ValleyBboxJson).RootElement,
            draft.RootElement.GetProperty("tracker").GetProperty("defaultBbox")));
        Assert.True(draftUpdatedAt > DateTimeOffset.UtcNow.AddHours(-1));
        var sections = await ReadMapSectionsAsync();
        Assert.Equal(2, sections.Count);
        Assert.Equal("{\"height\": 400}", sections[0]);
        Assert.Equal("{\"height\": 300}", sections[1]);
        Assert.Equal(versionsBefore, await ReadVersionsAsync());

        // A second run changes nothing.
        var counts = await ReadCountsAsync();
        await RunSqlAsync(A92TrackerMapsThemes.SeedSql() + A92TrackerMapsThemes.StripMapThemeKeysSql);
        Assert.Equal(counts, await ReadCountsAsync());
        Assert.Equal(sections, await ReadMapSectionsAsync());
        var (again, againUpdatedAt) = await ReadDraftAsync();
        Assert.Equal(draft.RootElement.GetRawText(), again.RootElement.GetRawText());
        Assert.Equal(draftUpdatedAt, againUpdatedAt);

        // Down to the baseline.
        await MigrateToAsync(BeforeA92);
        foreach (var table in Tables)
            Assert.Equal(0L, await ScalarAsync<long>($"select count(*) from information_schema.tables where table_name = '{table}';"));
        Assert.Equal(0L, await ScalarAsync<long>(
            "select count(*) from information_schema.columns where table_name = 'event' and column_name in ('tracker_bbox', 'tracker_map_id');"));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from pg_indexes where indexname = 'event_tracker_map';"));
        var restored = await ReadMapSectionsAsync();
        Assert.Equal(2, restored.Count);
        foreach (var data in restored.Values)
        {
            using var doc = JsonDocument.Parse(data);
            Assert.Equal(
                new[] { "standard", "expedition", "blizzard", "charcoal", "night", "nebula" },
                doc.RootElement.GetProperty("themes").EnumerateArray().Select(t => t.GetString()!).ToArray());
            Assert.Equal("night", doc.RootElement.GetProperty("defaultTheme").GetString());
        }
        var (down, _) = await ReadDraftAsync();
        Assert.False(down.RootElement.TryGetProperty("tracker", out _));
        Assert.Equal("Test site", down.RootElement.GetProperty("siteName").GetString());
        Assert.Equal(versionsBefore, await ReadVersionsAsync());
    }

    // Two events, a page with a map section carrying `themes` and
    // `defaultTheme` and one without them, a settings draft without
    // `tracker`, and a content version, as the pre-A92 schema wrote them.
    private const string SeedSql = @"
delete from section_item;
delete from section;
delete from page where slug = 'a92-live';
delete from location;
delete from cookie;
delete from event_status_history;
delete from event_message;
delete from event;
update site_setting_draft set data = '{""siteName"":""Test site""}'::jsonb, updated_at = now() - interval '1 day' where id = 1;
with p as (
  insert into page (slug, title, nav_label, nav_position, is_hidden, role, created_by, updated_by)
  values ('a92-live', 'Live', null, 90, false, 'none', 'seed', 'seed') returning id
)
insert into section (page_id, kind, position, is_hidden, data, presentation, updated_by)
select p.id, v.kind, v.position, false, v.data::jsonb, '{}'::jsonb, 'seed'
from p, (values
  ('map', 0, '{""themes"":[""night"",""standard""],""defaultTheme"":""night"",""height"":400}'),
  ('map', 1, '{""height"":300}')
) as v(kind, position, data);
insert into event (year, name, status_id, is_current, created_by, updated_at)
values (2025, 'Older', 1, false, 'seed', now()),
       (2026, 'Current', 1, true, 'seed', now());
insert into content_version (document, sha256, media_ids, published_by)
values ('{""settings"":{""siteName"":""Old""},""pages"":[{""sections"":[{""kind"":""map"",""data"":{""themes"":[""night""],""defaultTheme"":""night""}}]}]}'::jsonb, repeat('a', 64), '{}', 'seed');";

    private sealed record ThemeRow(string Renderer, string Name, int SortOrder, string StyleSha256, int StyleBytes,
        string Chrome, string Overlay, bool DefaultLight, bool DefaultDark);

    private async Task<Dictionary<(string, string), ThemeRow>> ReadThemesAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
select renderer, key, name, sort_order, style_sha256, style_bytes, chrome::text, overlay::text, default_light_mode, default_dark_mode
from tracker_theme;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new Dictionary<(string, string), ThemeRow>();
        while (await reader.ReadAsync())
            rows[(reader.GetString(0), reader.GetString(1))] = new ThemeRow(
                reader.GetString(0), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5),
                reader.GetString(6), reader.GetString(7), reader.GetBoolean(8), reader.GetBoolean(9));
        return rows;
    }

    private async Task<string> ReadCountsAsync() => await ScalarAsync<string>(@"
select concat_ws(',',
  (select count(*) from tracker_map), (select count(*) from tracker_theme),
  (select count(*) from event_tracker_theme), (select count(*) from tracker_theme_state),
  (select count(*) from event where tracker_map_id is null),
  (select string_agg(id || ':' || updated_at, ';' order by id) from tracker_theme));");

    // Every content version's row as text, in id order.
    private async Task<string?> ReadVersionsAsync() =>
        await ScalarAsync<string?>("select string_agg(c::text, '|' order by c.id) from content_version c;");

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
where p.slug = 'a92-live' and s.kind = 'map';", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var sections = new Dictionary<int, string>();
        while (await reader.ReadAsync())
            sections[reader.GetInt32(0)] = reader.GetString(1);
        return sections;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    private async Task RunSqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
