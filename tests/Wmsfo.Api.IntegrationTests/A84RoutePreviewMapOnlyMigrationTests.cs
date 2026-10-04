using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wmsfo.Api.Data;
using Wmsfo.Api.Data.Migrations;

namespace Wmsfo.Api.IntegrationTests;

// The A84RoutePreviewMapOnly migration (sql.md 14) removes `style` from every
// route_preview section's data, leaves a route_preview section without it and
// every other kind alone, and drops event.route_image_media_id. A second run
// of the strip changes nothing.
public sealed class A84RoutePreviewMapOnlyMigrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public A84RoutePreviewMapOnlyMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migration_strips_route_preview_style_and_drops_the_route_image_column()
    {
        await MigrateToAsync("A83GlobalLandmarks");
        await RunSqlAsync(@"
delete from section_item;
delete from section;
delete from page where slug = 'a84-route';
with p as (
  insert into page (slug, title, nav_label, nav_position, is_hidden, role, created_by, updated_by)
  values ('a84-route', 'Route', null, 90, false, 'none', 'seed', 'seed') returning id
)
insert into section (page_id, kind, position, is_hidden, data, presentation, updated_by)
select p.id, v.kind, v.position, false, v.data::jsonb, '{}'::jsonb, 'seed'
from p, (values
  ('route_preview', 0, '{""heading"":""With style"",""style"":""viewer"",""disclaimer"":null,""emptyText"":""Soon.""}'),
  ('route_preview', 1, '{""heading"":""Without style"",""disclaimer"":""A plan."",""emptyText"":""Soon.""}'),
  ('latest_message', 2, '{""heading"":""Latest update"",""style"":""card""}')
) as v(kind, position, data);");
        Assert.Contains("route_image_media_id", await ReadEventColumnsAsync());

        await MigrateToAsync(null);

        var sections = await ReadSectionsAsync();
        Assert.Equal(
            "{\"heading\": \"With style\", \"emptyText\": \"Soon.\", \"disclaimer\": null}",
            sections[0]);
        Assert.Equal(
            "{\"heading\": \"Without style\", \"emptyText\": \"Soon.\", \"disclaimer\": \"A plan.\"}",
            sections[1]);
        Assert.Equal("{\"style\": \"card\", \"heading\": \"Latest update\"}", sections[2]);
        Assert.DoesNotContain("route_image_media_id", await ReadEventColumnsAsync());

        // A second run changes nothing more.
        await RunSqlAsync(A84RoutePreviewMapOnly.StripRoutePreviewStyleSql);
        Assert.Equal(sections, await ReadSectionsAsync());
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

    // The test page's section data as text, keyed by position.
    private async Task<Dictionary<int, string>> ReadSectionsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
select s.position, s.data::text from section s
join page p on p.id = s.page_id
where p.slug = 'a84-route';", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var sections = new Dictionary<int, string>();
        while (await reader.ReadAsync()) sections[reader.GetInt32(0)] = reader.GetString(1);
        return sections;
    }

    private async Task<List<string>> ReadEventColumnsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select column_name from information_schema.columns where table_schema = 'public' and table_name = 'event';", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns;
    }

    private async Task RunSqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
