using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Wmsfo.Api.Data;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.IntegrationTests;

// The A148ThemeHeadingColor migration adds overlay.headingColor to every
// tracker_theme row that lacks it, by key for the seeded themes and #ffb300
// for any other row, and leaves a row that has it alone; its down removes the
// key. A fresh database and a migrated one agree with TrackerThemeSeed.
public sealed class A148ThemeHeadingColorMigrationTests : IClassFixture<PostgresFixture>
{
    private const string BeforeA148 = "A145SignupBlock";
    private readonly PostgresFixture _fixture;

    private static readonly Dictionary<string, string> Expected = new()
    {
        ["light"] = "#ffb300", ["dark"] = "#ffb300", ["standard"] = "#ffb300", ["expedition"] = "#ffb300",
        ["blizzard"] = "#ffb300", ["charcoal"] = "#69f0ae", ["night"] = "#ffb300", ["nebula"] = "#69f0ae",
    };

    public A148ThemeHeadingColorMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Up_backfills_every_row_without_the_key_and_down_removes_it()
    {
        await MigrateToAsync(BeforeA148);
        await RunSqlAsync("delete from tracker_theme where key like 'a148-%';");
        await RunSqlAsync("update tracker_theme set overlay = overlay - 'headingColor';");
        var seededOverlay = TrackerThemeSeed.Themes.Single(t => t.Key == "standard").Overlay;
        var bare = seededOverlay.Replace("\"headingColor\":\"#ffb300\",", "");
        var kept = seededOverlay.Replace("\"headingColor\":\"#ffb300\"", "\"headingColor\":\"#00e5ff\"");
        foreach (var (key, overlay) in new[] { ("a148-other", bare), ("a148-kept", kept) })
        {
            await RunSqlAsync($@"
insert into tracker_theme (renderer, key, name, sort_order, style_sha256, style_bytes, chrome, overlay, created_by, updated_by)
select renderer, '{key}', 'Theme {key}', 99, style_sha256, style_bytes, chrome, '{overlay}'::jsonb, 'a148-test', 'a148-test'
from tracker_theme where key = 'standard';");
        }
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from tracker_theme where overlay ? 'headingColor';"));

        await MigrateToAsync(null);
        var after = await HeadingColoursAsync();
        foreach (var (key, colour) in Expected) Assert.Equal(colour, after[key]);
        Assert.Equal("#ffb300", after["a148-other"]);
        Assert.Equal("#00e5ff", after["a148-kept"]);
        foreach (var seed in TrackerThemeSeed.Themes)
        {
            Assert.True(await ScalarAsync<bool>(
                $"select overlay = '{seed.Overlay}'::jsonb from tracker_theme where renderer = '{seed.Renderer}' and key = '{seed.Key}';"),
                $"the migrated {seed.Key} overlay differs from TrackerThemeSeed");
        }

        await MigrateToAsync(BeforeA148);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from tracker_theme where overlay ? 'headingColor';"));

        await MigrateToAsync(null);
        await RunSqlAsync("delete from tracker_theme where key like 'a148-%';");
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from tracker_theme where not overlay ? 'headingColor';"));
    }

    [Fact]
    public async Task The_column_comment_names_the_eight_keys()
    {
        await MigrateToAsync(null);
        var comment = await ScalarAsync<string>(
            "select col_description('tracker_theme'::regclass, (select attnum from pg_attribute where attrelid = 'tracker_theme'::regclass and attname = 'overlay'));");
        Assert.StartsWith(
            "{ routeColor, routeOpacity, arrowColor, timeLabelBg, timeLabelFg, timeLabelOpacity, userColor, headingColor }.",
            comment);
    }

    private async Task<Dictionary<string, string>> HeadingColoursAsync()
    {
        var result = new Dictionary<string, string>();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select key, overlay->>'headingColor' from tracker_theme;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    private async Task MigrateToAsync(string? target)
    {
        var options = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.MigrateConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(target);
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
