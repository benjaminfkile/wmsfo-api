using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Wmsfo.Api.Data;
using Wmsfo.Api.Data.Migrations;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.IntegrationTests;

// The A105ValleyTerrainZoom15 migration moves the seeded valley map from
// terrain zoom 13 to 15 with the package key of the identity at 15, and its
// down restores 13 and the key at 13. A fresh database, whose A92 insert
// writes the row at 13, ends at 15 with the same key.
public sealed class A105ValleyTerrainZoom15MigrationTests : IClassFixture<PostgresFixture>
{
    private const string BeforeA105 = "A99TerrainMaxZoom15";
    private readonly PostgresFixture _fixture;

    public A105ValleyTerrainZoom15MigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Up_moves_a_seeded_row_at_13_to_15_and_down_restores_it()
    {
        await MigrateToAsync(BeforeA105);
        await RunSqlAsync(
            $"update tracker_map set terrain_max_zoom = 13, package_key = '{TrackerThemeSeed.ValleyInsertPackageKey}' where prefix = 'basemap';");
        Assert.Equal("13:" + TrackerThemeSeed.ValleyInsertPackageKey, await ReadValleyAsync());

        await MigrateToAsync(null);
        Assert.Equal("15:" + TrackerThemeSeed.ValleyPackageKey, await ReadValleyAsync());

        await MigrateToAsync(BeforeA105);
        Assert.Equal("13:" + TrackerThemeSeed.ValleyInsertPackageKey, await ReadValleyAsync());

        await MigrateToAsync(null);
        Assert.Equal("15:" + TrackerThemeSeed.ValleyPackageKey, await ReadValleyAsync());
    }

    [Fact]
    public async Task A_fresh_database_holds_the_seeded_row_at_15()
    {
        await MigrateToAsync("A89HelpTopics");
        await MigrateToAsync(null);
        Assert.Equal("15:" + TrackerThemeSeed.ValleyPackageKey, await ReadValleyAsync());
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from tracker_map where prefix = 'basemap';"));

        // A second run of the up changes nothing.
        await RunSqlAsync(A105ValleyTerrainZoom15.UpSql);
        Assert.Equal("15:" + TrackerThemeSeed.ValleyPackageKey, await ReadValleyAsync());
    }

    [Fact]
    public void The_seeded_key_is_the_key_of_the_identity_at_15_and_the_down_key_at_13()
    {
        Assert.Equal(15, TrackerThemeSeed.ValleyTerrainMaxZoom);
        Assert.Equal(TrackerThemeSeed.ValleyPackageKey, TrackerThemeSeed.PackageKeyOf(TrackerThemeSeed.ValleyIdentity));
        Assert.Equal(TrackerThemeSeed.ValleyInsertPackageKey,
            TrackerThemeSeed.PackageKeyOf(TrackerThemeSeed.ValleyIdentity with { TerrainMaxZoom = TrackerThemeSeed.ValleyInsertTerrainMaxZoom }));
    }

    // The valley row's terrain zoom and package key as "zoom:key".
    private async Task<string> ReadValleyAsync() => await ScalarAsync<string>(
        "select terrain_max_zoom || ':' || package_key from tracker_map where prefix = 'basemap';");

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
