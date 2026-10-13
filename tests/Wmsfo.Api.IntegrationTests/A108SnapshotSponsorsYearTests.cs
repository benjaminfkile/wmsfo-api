using System.Net;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Data;

namespace Wmsfo.Api.IntegrationTests;

// A108 acceptance (contracts 1.3 sponsors[] and sponsorsYear):
//   - no current event: sponsors[] is the greatest year with a listable row
//     (active, not anonymous, can advertise), in snapshot order, sponsorsYear
//     names it, and its logos are in media.
//   - a current event: sponsors[] is the event's year even when a later year
//     has listable rows, and sponsorsYear is the event's year.
//   - no listable row anywhere: sponsors[] is empty and sponsorsYear null.
//   - sponsorsYear sits right after sponsors and the built snapshot validates
//     against the snapshot schema.
public sealed class A108SnapshotSponsorsYearTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A25Host? _host;

    public A108SnapshotSponsorsYearTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(contextOptions))
            await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from cookie;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from location;",
            "delete from event;",
            "delete from sponsor_year;",
            "delete from sponsor;",
            "delete from media_asset;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
        _host = await A25Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task No_current_event_lists_the_greatest_listable_year_with_its_logos()
    {
        var old = await CreateSponsorAsync("Old");
        var bravo = await CreateSponsorAsync("Bravo");
        var alpha = await CreateSponsorAsync("Alpha");
        var hidden = await CreateSponsorAsync("Hidden");
        await UpsertYearAsync(old, 2024, 900m);
        await UpsertYearAsync(bravo, 2025, 500m);
        await UpsertYearAsync(alpha, 2025, 100m);
        await UpsertYearAsync(hidden, 2025, 1000m, anonymous: true);

        var oldLogo = await InsertReadyMediaAsync("old.jpg");
        var bravoLogo = await InsertReadyMediaAsync("bravo.jpg");
        var alphaLogo = await InsertReadyMediaAsync("alpha.jpg");
        var hiddenLogo = await InsertReadyMediaAsync("hidden.jpg");
        await SetLogoAsync(old, oldLogo);
        await SetLogoAsync(bravo, bravoLogo);
        await SetLogoAsync(alpha, alphaLogo);
        await SetLogoAsync(hidden, hiddenLogo);

        using var doc = await ReadSnapshotAsync();
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("event").ValueKind);
        Assert.Equal(new[] { "Bravo", "Alpha" },
            root.GetProperty("sponsors").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray());
        Assert.Equal(2025, root.GetProperty("sponsorsYear").GetInt32());

        var media = root.GetProperty("media");
        Assert.True(media.TryGetProperty(bravoLogo.ToString(), out _));
        Assert.True(media.TryGetProperty(alphaLogo.ToString(), out _));
        Assert.False(media.TryGetProperty(oldLogo.ToString(), out _));
        Assert.False(media.TryGetProperty(hiddenLogo.ToString(), out _));

        var keys = root.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(keys.IndexOf("sponsors") + 1, keys.IndexOf("sponsorsYear"));
        AssertValid(root);
    }

    [Fact]
    public async Task A_current_event_lists_its_own_year_over_a_later_listable_year()
    {
        await CreateCurrentEventAsync(2026);
        var current = await CreateSponsorAsync("Current");
        var later = await CreateSponsorAsync("Later");
        await UpsertYearAsync(current, 2026, 100m);
        await UpsertYearAsync(later, 2027, 500m);

        using var doc = await ReadSnapshotAsync();
        var root = doc.RootElement;
        Assert.Equal(new[] { "Current" },
            root.GetProperty("sponsors").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray());
        Assert.Equal(2026, root.GetProperty("sponsorsYear").GetInt32());
        AssertValid(root);
    }

    [Fact]
    public async Task No_listable_row_gives_empty_sponsors_and_null_sponsorsYear()
    {
        var hidden = await CreateSponsorAsync("Hidden");
        var inactive = await CreateSponsorAsync("Inactive");
        var quiet = await CreateSponsorAsync("Quiet");
        await UpsertYearAsync(hidden, 2025, 100m, anonymous: true);
        await UpsertYearAsync(inactive, 2025, 100m, active: false);
        await UpsertYearAsync(quiet, 2025, 100m, canAdvertise: false);

        using var doc = await ReadSnapshotAsync();
        var root = doc.RootElement;
        Assert.Empty(root.GetProperty("sponsors").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sponsorsYear").ValueKind);
        AssertValid(root);
    }

    private static void AssertValid(JsonElement instance)
    {
        var schemaText = File.ReadAllText(Path.Combine(TestPaths.ContractsDir, "schema", "snapshot.schema.json"));
        var schema = JsonSchema.FromText(schemaText);
        var results = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid) return;
        var details = results.Details ?? Enumerable.Empty<EvaluationResults>();
        var messages = string.Join("\n", details
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => (d.Errors ?? new Dictionary<string, string>()).Select(e => $"{d.InstanceLocation}: {e.Key}={e.Value}")));
        Assert.Fail($"snapshot schema did not accept the built snapshot:\n{messages}");
    }

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string? body = null)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<long> CreateSponsorAsync(string name)
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/sponsors", $"{{\"name\":\"{name}\"}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return dto.RootElement.GetProperty("id").GetInt64();
    }

    private async Task UpsertYearAsync(long sponsorId, int year, decimal amount,
        bool active = true, bool canAdvertise = true, bool anonymous = false)
    {
        var body = "{\"amountDonated\":" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"active\":" + (active ? "true" : "false") +
            ",\"canAdvertise\":" + (canAdvertise ? "true" : "false") +
            ",\"anonymous\":" + (anonymous ? "true" : "false") +
            ",\"pinnedPosition\":null,\"lingerMsOverride\":null}";
        var response = await SendAdminAsync(HttpMethod.Put, $"/admin/sponsors/{sponsorId}/years/{year}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> InsertReadyMediaAsync(string filename)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into media_asset (id, filename, content_type, kind, state, s3_key, uploaded_by, alt, title, variants, width, height)
values ($1, $2, 'image/jpeg', 'raster', 'ready',
        'media/' || $1::text || '/' || $2, 'test', 'logo', '', '{}'::jsonb, 400, 200);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = filename });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task SetLogoAsync(long sponsorId, Guid mediaId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("update sponsor set logo_media_id = $1 where id = $2;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = sponsorId });
        await cmd.ExecuteNonQueryAsync();
    }

    // Inserts the current event through SQL; the next [snapshot] write picks it up.
    private async Task CreateCurrentEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, 1, true, 'seed', now());", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        await cmd.ExecuteNonQueryAsync();
    }

    // A settings write is a [snapshot] write, so it rebuilds the snapshot; the
    // object is then read back from the recording store under snapshot.url.
    private async Task<JsonDocument> ReadSnapshotAsync()
    {
        var response = await SendAdminAsync(HttpMethod.Put, "/admin/settings/poll_interval_ms", "{\"value\":5000}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string url;
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("select url from snapshot where id = 1;", conn);
            url = (string)(await cmd.ExecuteScalarAsync() ?? "");
        }
        Assert.False(string.IsNullOrEmpty(url));
        var key = "snapshots/" + url.Substring(url.LastIndexOf('/') + 1);
        var content = await _host!.Store.GetObjectAsync(key);
        Assert.NotNull(content);
        return JsonDocument.Parse(content!.Bytes);
    }
}
