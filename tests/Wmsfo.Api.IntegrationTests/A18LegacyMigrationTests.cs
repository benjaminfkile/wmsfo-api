using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Wmsfo.Api.Data;
using Wmsfo.Api.Objects;
using Wmsfo.Migrate;

namespace Wmsfo.Api.IntegrationTests;

// A18 acceptance criteria (task 289): the legacy migration tool, run twice
// against a legacy-shaped schema, is idempotent (second run inserts nothing),
// runs the legacy logo through the confirm pipeline, and prints the
// verification queries at the end.
public sealed class A18LegacyMigrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private string _targetStoreRoot = "";
    private LocalObjectStore _store = default!;
    private InMemoryLegacyLogoSource _legacyLogos = default!;

    public A18LegacyMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _targetStoreRoot = Path.Combine(Path.GetTempPath(), "wmsfo-a18-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_targetStoreRoot);
        _store = new LocalObjectStore(_targetStoreRoot, "https://api.example.com");

        // Fresh target DB via EF migrations. Every test in this class rebuilds
        // the target so idempotency claims start from a known baseline.
        var opts = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(opts))
        {
            await db.Database.MigrateAsync();
        }

        // Reset target and legacy state so tests inside the same fixture stay
        // independent. The PostgresFixture creates the database once per test
        // class, so per-test cleanup is our job.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await A18LegacySchema.ClearTargetAsync(conn);
        await A18LegacySchema.CreateAsync(conn);
        await A18LegacySchema.SeedAsync(conn);

        _legacyLogos = new InMemoryLegacyLogoSource(new Dictionary<string, byte[]>
        {
            ["legacy/sponsors/1/logo.png"] = BuildPng(200, 100),
        });
    }

    public async Task DisposeAsync()
    {
        try { Directory.Delete(_targetStoreRoot, recursive: true); } catch { }
        await Task.CompletedTask;
    }

    // ---------------- acceptance criterion 822: second run writes nothing ----------------

    [Fact]
    public async Task Second_run_writes_nothing()
    {
        var options = BuildOptions();
        var out1 = new StringWriter();
        var out2 = new StringWriter();

        var migrator1 = new LegacyMigrator(options, _store, _legacyLogos, out1);
        var s1 = await migrator1.RunAsync(CancellationToken.None);
        Assert.True(s1.TotalInserted > 0, "first run must insert rows");

        // The first run should have added: a legacy beacon, sponsors, sponsor_years,
        // one sponsor_setting, per-year events, the 2025 route, event_messages,
        // funds updates, contact messages, and one legacy media asset.
        Assert.Equal(1, s1.Step("legacy_beacon")!.Inserted);
        Assert.True(s1.Step("sponsors")!.Inserted >= 2);
        Assert.True(s1.Step("sponsor_years")!.Inserted >= 2);
        Assert.Equal(1, s1.Step("sponsor_setting")!.Inserted);
        Assert.True(s1.Step("flight_history")!.Inserted >= 2);
        Assert.Equal(1, s1.Step("route_2025")!.Inserted);
        Assert.True(s1.Step("event_messages")!.Inserted >= 1);
        Assert.True(s1.Step("funds_percent")!.Inserted >= 1);
        Assert.True(s1.Step("contact_messages")!.Inserted >= 1);
        Assert.True(s1.Step("legacy_logos")!.Inserted >= 1);

        // Persist the counts we can check row-by-row afterwards.
        var (events1, locations1, sponsors1, sponsorYears1, msgs1, contacts1, routes1, media1, beacons1) =
            await CountsAsync();

        // Second run - every step must be a no-op.
        var migrator2 = new LegacyMigrator(options, _store, _legacyLogos, out2);
        var s2 = await migrator2.RunAsync(CancellationToken.None);
        Assert.Equal(0, s2.TotalInserted);
        foreach (var step in s2.Steps)
        {
            Assert.True(step.Inserted == 0, $"step {step.Name} inserted {step.Inserted} on second run");
        }

        // Counts unchanged.
        var (events2, locations2, sponsors2, sponsorYears2, msgs2, contacts2, routes2, media2, beacons2) =
            await CountsAsync();
        Assert.Equal(events1, events2);
        Assert.Equal(locations1, locations2);
        Assert.Equal(sponsors1, sponsors2);
        Assert.Equal(sponsorYears1, sponsorYears2);
        Assert.Equal(msgs1, msgs2);
        Assert.Equal(contacts1, contacts2);
        Assert.Equal(routes1, routes2);
        Assert.Equal(media1, media2);
        Assert.Equal(beacons1, beacons2);
    }

    // ---------------- acceptance criterion 823: verification queries print ----------------

    [Fact]
    public async Task Verification_queries_print_after_run()
    {
        var options = BuildOptions();
        var writer = new StringWriter();
        var migrator = new LegacyMigrator(options, _store, _legacyLogos, writer);
        await migrator.RunAsync(CancellationToken.None);
        var output = writer.ToString();

        Assert.Contains("=== verification queries ===", output);
        Assert.Contains("-- row counts", output);
        Assert.Contains("-- event details", output);
        Assert.Contains("-- sponsor logos", output);
        Assert.Contains("-- app_setting sponsor_linger_ms_per_dollar", output);

        // The row-counts query names every migrated table.
        foreach (var table in new[] { "event", "location", "sponsor", "sponsor_year",
                                       "event_message", "contact_message", "route", "media_asset", "beacon" })
        {
            Assert.Contains(table, output);
        }
    }

    // ---------------- legacy logo import through the confirm slice ----------------

    [Fact]
    public async Task Sponsor_logo_becomes_ready_media_asset_and_relinks()
    {
        var options = BuildOptions();
        var migrator = new LegacyMigrator(options, _store, _legacyLogos, new StringWriter());
        await migrator.RunAsync(CancellationToken.None);

        // The media_asset row lands as ready with title 'legacy:<legacy_key>'.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        Guid mediaId;
        await using (var cmd = new NpgsqlCommand(
            "select id, state, title, kind, alt, sha256, uploaded_by from media_asset where title = $1;", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "legacy:legacy/sponsors/1/logo.png" });
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "legacy media_asset row missing");
            mediaId = reader.GetGuid(0);
            Assert.Equal("ready", reader.GetString(1));
            Assert.Equal("legacy:legacy/sponsors/1/logo.png", reader.GetString(2));
            Assert.Equal("raster", reader.GetString(3));
            Assert.Equal("Sponsor One", reader.GetString(4));
            Assert.False(string.IsNullOrEmpty(reader.GetString(5)));
            Assert.Equal("migration", reader.GetString(6));
        }
        // The sponsor is linked (fresh command after the reader is closed).
        await using (var linkCmd = new NpgsqlCommand(
            "select logo_media_id from sponsor where id = 1;", conn))
        {
            var linked = await linkCmd.ExecuteScalarAsync();
            Assert.IsType<Guid>(linked);
            Assert.Equal(mediaId, (Guid)linked!);
        }

        // The bytes and the header sidecars sit in the local store.
        var storePath = Directory.EnumerateFiles(_targetStoreRoot, "*.png", SearchOption.AllDirectories).Single();
        Assert.EndsWith(".png", storePath);
    }

    // ---------------- flight_history → event + locations ----------------

    [Fact]
    public async Task Flight_history_becomes_one_event_per_year_with_locations_renumbered()
    {
        var options = BuildOptions();
        await new LegacyMigrator(options, _store, _legacyLogos, new StringWriter())
            .RunAsync(CancellationToken.None);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        // The seed puts 3 rows into 2024 and 3 rows into 2025, so 3 locations per event.
        await using (var evCmd = new NpgsqlCommand(
            "select year, status_id, is_current, next_seq, name from event order by year;", conn))
        {
            var events = new List<(int Year, int Status, bool Current, long NextSeq, string Name)>();
            await using var reader = await evCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                events.Add((reader.GetInt32(0), reader.GetInt16(1), reader.GetBoolean(2), reader.GetInt64(3), reader.GetString(4)));
            }
            Assert.Equal(2, events.Count);
            foreach (var e in events)
            {
                Assert.Equal(4, e.Status);
                Assert.False(e.Current);
                Assert.Equal(3 + 1, e.NextSeq);
                Assert.Equal($"Santa Flyover {e.Year}", e.Name);
            }
        }

        await using (var seqCmd = new NpgsqlCommand(
            @"select e.year, l.seq, l.published
              from location l join event e on e.id = l.event_id
              order by e.year, l.seq;", conn))
        {
            var rows = new List<(int Year, long Seq, bool Published)>();
            await using var reader = await seqCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add((reader.GetInt32(0), reader.GetInt64(1), reader.GetBoolean(2)));
            Assert.Equal(6, rows.Count);
            Assert.All(rows, r => Assert.True(r.Published));
            Assert.Equal(new long[] { 1, 2, 3 }, rows.Where(r => r.Year == 2024).Select(r => r.Seq).ToArray());
            Assert.Equal(new long[] { 1, 2, 3 }, rows.Where(r => r.Year == 2025).Select(r => r.Seq).ToArray());
        }
    }

    // ---------------- 2025 route is uploaded and event.route_id is linked ----------------

    [Fact]
    public async Task Route_2025_uploads_object_and_links_event()
    {
        var options = BuildOptions();
        await new LegacyMigrator(options, _store, _legacyLogos, new StringWriter())
            .RunAsync(CancellationToken.None);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
            "select r.s3_key, r.point_count, r.uploaded_by, e.route_id from route r join event e on e.route_id = r.id where e.year = 2025;", conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "route/link missing");
            var s3Key = reader.GetString(0);
            Assert.Equal(3, reader.GetInt32(1));
            Assert.Equal("migration", reader.GetString(2));
            var routePath = Path.Combine(_targetStoreRoot, s3Key.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(routePath), $"route object missing at {routePath}");
        }
    }

    // ---------------- funds_percent and contact_message idempotency ----------------

    [Fact]
    public async Task Funds_percent_reflects_latest_row_per_year()
    {
        var options = BuildOptions();
        await new LegacyMigrator(options, _store, _legacyLogos, new StringWriter())
            .RunAsync(CancellationToken.None);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select year, funds_percent from event order by year;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var got = new Dictionary<int, int>();
        while (await reader.ReadAsync()) got[reader.GetInt32(0)] = reader.GetInt32(1);
        // Seed writes two funds rows for 2024 (30 then 75) and one for 2025 (50).
        Assert.Equal(75, got[2024]);
        Assert.Equal(50, got[2025]);
    }

    // ---------------- dry-run writes nothing ----------------

    [Fact]
    public async Task Dry_run_writes_nothing()
    {
        var options = BuildOptions() with { DryRun = true };
        Assert.True(options.DryRun);
        var writer = new StringWriter();
        var migrator = new LegacyMigrator(options, _store, _legacyLogos, writer);
        var summary = await migrator.RunAsync(CancellationToken.None);
        Assert.Equal(0, summary.TotalInserted);

        // No event, no location, no media_asset ever written.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var table in new[] { "event", "location", "sponsor", "sponsor_year",
                                       "event_message", "contact_message", "route", "media_asset" })
        {
            await using var cmd = new NpgsqlCommand($"select count(*) from {table};", conn);
            var scalar = await cmd.ExecuteScalarAsync();
            var count = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            Assert.Equal(0L, count);
        }
        // No object bytes in the target store either.
        Assert.Empty(Directory.EnumerateFiles(_targetStoreRoot, "*", SearchOption.AllDirectories));
    }

    // ---------------- helpers ----------------

    private MigrateOptions BuildOptions() => new()
    {
        LegacyConnection = _fixture.ConnectionString,
        TargetConnection = _fixture.ConnectionString,
        Bucket = "wmsfo-test",
        CdnBaseUrl = "https://cdn.example",
        EventNameFormat = MigrateOptions.DefaultEventNameFormat,
    };

    private async Task<(int Events, int Locations, int Sponsors, int SponsorYears,
        int EventMessages, int ContactMessages, int Routes, int MediaAssets, int Beacons)> CountsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        async Task<int> Count(string table)
        {
            await using var cmd = new NpgsqlCommand($"select count(*) from {table};", conn);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        return (
            await Count("event"),
            await Count("location"),
            await Count("sponsor"),
            await Count("sponsor_year"),
            await Count("event_message"),
            await Count("contact_message"),
            await Count("route"),
            await Count("media_asset"),
            await Count("beacon"));
    }

    private static byte[] BuildPng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 40, 40, 255));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }
}
