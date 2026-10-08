using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.IntegrationTests;

// A94: ThemeStyles loads and hash-checks the eight seeded style bodies, and
// EnsureWrittenAsync (sql.md 8.16 step 2b) writes the missing seeded objects
// with the immutable header, stamps tracker_theme_state, heals a lost object,
// and logs the marker for a missing object whose body is not seeded.
public sealed class A94ThemeStylesBootTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public A94ThemeStylesBootTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(options))
        {
            await db.Database.MigrateAsync();
        }
        await ExecAsync("update tracker_theme_state set written_at = null where id = 1;");
        await ExecAsync("delete from tracker_theme where created_by = 'a94-test';");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static ThemeStyles LoadStyles() => ThemeStyles.Load(TestPaths.RepoRoot);

    [Fact]
    public async Task First_run_writes_eight_objects_and_stamps_then_a_second_run_writes_nothing()
    {
        var styles = LoadStyles();
        var store = new InMemoryObjectStore();
        var logger = new ListLogger();

        await using var conn = await OpenAsync();
        var first = await styles.EnsureWrittenAsync(conn, store, logger);
        Assert.Equal(8, first.Written);
        Assert.Equal(0, first.Missing);
        Assert.Equal(8, store.PutKeys.Count);
        foreach (var theme in TrackerThemeSeed.Themes)
        {
            var key = "themes/" + theme.StyleSha256 + ".json";
            Assert.True(store.Objects.TryGetValue(key, out var stored), key);
            Assert.Equal("application/json; charset=utf-8", stored!.ContentType);
            Assert.Equal("public, max-age=31536000, immutable", stored.CacheControl);
            var file = await File.ReadAllBytesAsync(Path.Combine(TestPaths.ContractsDir, "fixtures", "themes", theme.Key + ".json"));
            Assert.Equal(file, stored.Bytes);
            Assert.Equal(theme.StyleBytes, stored.Bytes.Length);
        }
        var stamp = await WrittenAtAsync(conn);
        Assert.NotNull(stamp);

        var second = await styles.EnsureWrittenAsync(conn, store, logger);
        Assert.Equal(0, second.Written);
        Assert.Equal(8, second.Present);
        Assert.Equal(8, store.PutKeys.Count);
        Assert.Equal(stamp, await WrittenAtAsync(conn));
        Assert.DoesNotContain(logger.Lines, l => l.Contains(LogMarkers.ThemeStyleMissing, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deleted_seed_object_is_rewritten()
    {
        var styles = LoadStyles();
        var store = new InMemoryObjectStore();
        await using var conn = await OpenAsync();
        await styles.EnsureWrittenAsync(conn, store, new ListLogger());

        var night = TrackerThemeSeed.Themes.Single(t => t.Key == "night");
        var key = "themes/" + night.StyleSha256 + ".json";
        await store.DeleteObjectAsync(key);
        store.PutKeys.Clear();

        var result = await styles.EnsureWrittenAsync(conn, store, new ListLogger());
        Assert.Equal(1, result.Written);
        Assert.Equal(new[] { key }, store.PutKeys.ToArray());
        Assert.True(store.Objects.ContainsKey(key));
    }

    [Fact]
    public async Task A_missing_object_with_a_non_seeded_hash_logs_the_marker_and_writes_nothing()
    {
        var styles = LoadStyles();
        var store = new InMemoryObjectStore();
        await using var conn = await OpenAsync();
        await styles.EnsureWrittenAsync(conn, store, new ListLogger());
        store.PutKeys.Clear();
        var before = await WrittenAtAsync(conn);

        var sha = new string('a', 64);
        long id;
        await using (var cmd = new NpgsqlCommand(@"
insert into tracker_theme (renderer, key, name, style_sha256, style_bytes, chrome, overlay, created_by, updated_by)
select 'google', 'a94-extra', 'A94 extra', $1, 2, chrome, overlay, 'a94-test', 'a94-test'
from tracker_theme where key = 'standard'
returning id;", conn))
        {
            cmd.Parameters.AddWithValue(sha);
            id = (long)(await cmd.ExecuteScalarAsync())!;
        }

        var logger = new ListLogger();
        var result = await styles.EnsureWrittenAsync(conn, store, logger);
        Assert.Equal(0, result.Written);
        Assert.Equal(1, result.Missing);
        Assert.Empty(store.PutKeys);
        Assert.False(store.Objects.ContainsKey("themes/" + sha + ".json"));
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(LogMarkers.ThemeStyleMissing, warning.Message, StringComparison.Ordinal);
        Assert.Contains("themeId=" + id, warning.Message, StringComparison.Ordinal);
        Assert.Equal(before, await WrittenAtAsync(conn));
    }

    [Fact]
    public void A_tampered_fixture_fails_the_load()
    {
        var root = Path.Combine(Path.GetTempPath(), "wmsfo-themes-" + Guid.NewGuid().ToString("N")[..12]);
        var dir = Path.Combine(root, "contracts", "fixtures", "themes");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(TestPaths.ContractsDir, "fixtures", "themes")))
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
            Assert.Equal(root, ThemeStyles.ResolveRoot(Path.Combine(root, "contracts")));
            Assert.Equal(8, ThemeStyles.Load(root).Bodies.Count);

            var path = Path.Combine(dir, "charcoal.json");
            var text = File.ReadAllText(path);
            Assert.Contains("\"stylers\"", text, StringComparison.Ordinal);
            File.WriteAllText(path, text.Replace("\"stylers\":[", "\"stylers\":[{\"visibility\":\"off\"},", StringComparison.Ordinal));

            var ex = Assert.Throws<InvalidDataException>(() => ThemeStyles.Load(root));
            Assert.Contains("charcoal.json", ex.Message, StringComparison.Ordinal);
            Assert.Contains("style_sha256", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<DateTimeOffset?> WrittenAtAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("select written_at from tracker_theme_state where id = 1;", conn);
        var r = await cmd.ExecuteScalarAsync();
        return r is DateTime dt ? new DateTimeOffset(dt, TimeSpan.Zero) : r as DateTimeOffset?;
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IEnumerable<string> Lines => Entries.Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
