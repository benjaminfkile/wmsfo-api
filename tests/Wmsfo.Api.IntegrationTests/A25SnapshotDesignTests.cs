using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Data;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// A25 acceptance:
//   - Snapshot sponsors[] order: pinned first (position asc), then amount desc,
//     name asc, id asc.
//   - lingerMsOverride beats the formula (contracts 1.3).
//   - flightHistory: null when the event has no route_id; present and thinned
//     (7 points at max 3 -> points 1, 4, 7) when the event links a route.
//   - routeImageMediaId: the poster id present on event and its asset in media.
//   - PATCH /admin/events/{id} { routeId } rebuilds the snapshot with the new
//     flightHistory on the next snapshot.
public sealed class A25SnapshotDesignTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A25Host? _host;

    public A25SnapshotDesignTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAndCleanAsync();
        _host = await A25Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task MigrateAndCleanAsync()
    {
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(contextOptions);
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
            "delete from route;",
            "delete from media_asset;",
            "delete from app_setting where key in ('sponsor_linger_ms_per_dollar','sponsor_linger_min_ms','flight_history_max_points');",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
    }

    // ---------- sponsors[] order: two pinned, three unpinned ----------

    [Fact]
    public async Task Snapshot_sponsors_order_pins_first_then_amount_desc_name_id()
    {
        await CreateCurrentEventAsync(2027);

        var alpha = await CreateSponsorAsync("Alpha");     // pin 2
        var bravo = await CreateSponsorAsync("Bravo");     // amount 500
        var charlie = await CreateSponsorAsync("Charlie"); // amount 200
        var delta = await CreateSponsorAsync("Delta");     // amount 200 (tie, name asc)
        var echo = await CreateSponsorAsync("Echo");       // pin 1

        await UpsertYearAsync(alpha, 2027, 50m, pinnedPosition: 2);
        await UpsertYearAsync(bravo, 2027, 500m);
        await UpsertYearAsync(charlie, 2027, 200m);
        await UpsertYearAsync(delta, 2027, 200m);
        await UpsertYearAsync(echo, 2027, 10m, pinnedPosition: 1);

        var snapshot = await ReadSnapshotAsync();
        var sponsors = snapshot.RootElement.GetProperty("sponsors").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Equal(new[] { "Echo", "Alpha", "Bravo", "Charlie", "Delta" }, sponsors);
    }

    // ---------- sponsors[].lingerMs: override beats formula ----------

    [Fact]
    public async Task Snapshot_lingerMs_uses_override_when_set()
    {
        await CreateCurrentEventAsync(2027);
        await PutSettingAsync("sponsor_linger_ms_per_dollar", 40);
        await PutSettingAsync("sponsor_linger_min_ms", 2000);

        var over = await CreateSponsorAsync("Over");
        var formula = await CreateSponsorAsync("Formula");

        // Formula: 100 * 40 = 4000. Override 9000 must win over that (and over the min).
        await UpsertYearAsync(over, 2027, 100m, lingerMsOverride: 9000);
        await UpsertYearAsync(formula, 2027, 100m);

        var snapshot = await ReadSnapshotAsync();
        var byName = snapshot.RootElement.GetProperty("sponsors").EnumerateArray()
            .ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("lingerMs").GetInt32());
        Assert.Equal(9000, byName["Over"]);
        Assert.Equal(4000, byName["Formula"]);
    }

    // ---------- event.flightHistory: null when unlinked ----------

    [Fact]
    public async Task Snapshot_flightHistory_null_when_no_route_linked()
    {
        await CreateCurrentEventAsync(2027);
        // Force a snapshot rebuild without linking a route.
        await CreateSponsorAsync("Trigger");
        var snapshot = await ReadSnapshotAsync();
        Assert.Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("event")
            .GetProperty("flightHistory").ValueKind);
    }

    // ---------- event.flightHistory: 7 points at max 3 -> 1, 4, 7 ----------

    [Fact]
    public async Task Snapshot_flightHistory_thins_seven_points_to_indices_1_4_7()
    {
        var eventId = await CreateCurrentEventAsync(2027);
        // The admin PUT clamps this at 100 (contracts 6); insert 3 directly so
        // the 7-point / max-3 thinning example from contracts 1.3 is testable.
        await SetSettingDirectAsync("flight_history_max_points", 3);

        var routePoints = Enumerable.Range(1, 7)
            .Select(i => new RoutePoint
            {
                Lat = 46.0 + i * 0.001,
                Lng = -114.0 + i * 0.001,
                RecordedAt = DateTimeOffset.Parse($"2025-12-22T01:31:0{i}.000Z"),
            })
            .ToList();
        var routeId = await UploadRouteAsync("Test flight", routePoints);
        await LinkRouteAsync(eventId, routeId);

        var snapshot = await ReadSnapshotAsync();
        var fh = snapshot.RootElement.GetProperty("event").GetProperty("flightHistory");
        Assert.Equal(routeId, fh.GetProperty("routeId").GetInt64());
        Assert.Equal("Test flight", fh.GetProperty("name").GetString());
        var points = fh.GetProperty("points").EnumerateArray().ToArray();
        Assert.Equal(3, points.Length);
        AssertPointEquals(routePoints[0], points[0]);
        AssertPointEquals(routePoints[3], points[1]);
        AssertPointEquals(routePoints[6], points[2]);
    }

    // ---------- event.routeImageMediaId + media map ----------

    [Fact]
    public async Task Snapshot_routeImageMediaId_present_and_asset_in_media_map()
    {
        var eventId = await CreateCurrentEventAsync(2027);
        var posterId = await InsertReadyRasterMediaAsync("poster.jpg");
        await PatchEventAsync(eventId, $"{{\"routeImageMediaId\":\"{posterId}\"}}");

        var snapshot = await ReadSnapshotAsync();
        var ev = snapshot.RootElement.GetProperty("event");
        Assert.Equal(posterId.ToString(), ev.GetProperty("routeImageMediaId").GetString());
        var media = snapshot.RootElement.GetProperty("media");
        Assert.True(media.TryGetProperty(posterId.ToString(), out var entry));
        Assert.Equal("raster", entry.GetProperty("kind").GetString());
    }

    // ---------- PATCH { routeId } rebuilds flightHistory on next snapshot ----------

    [Fact]
    public async Task Patch_routeId_rebuilds_flightHistory_on_next_snapshot()
    {
        var eventId = await CreateCurrentEventAsync(2027);

        // First route: 3 points, "Route A".
        var routeAPoints = new List<RoutePoint>
        {
            new() { Lat = 46.0, Lng = -114.0, RecordedAt = DateTimeOffset.Parse("2025-12-22T01:31:00.000Z") },
            new() { Lat = 46.1, Lng = -114.1, RecordedAt = DateTimeOffset.Parse("2025-12-22T01:31:10.000Z") },
            new() { Lat = 46.2, Lng = -114.2, RecordedAt = DateTimeOffset.Parse("2025-12-22T01:31:20.000Z") },
        };
        var routeA = await UploadRouteAsync("Route A", routeAPoints);
        await LinkRouteAsync(eventId, routeA);
        var snapA = await ReadSnapshotAsync();
        var fhA = snapA.RootElement.GetProperty("event").GetProperty("flightHistory");
        Assert.Equal(routeA, fhA.GetProperty("routeId").GetInt64());
        Assert.Equal("Route A", fhA.GetProperty("name").GetString());

        // Second route: 4 points, "Route B". PATCH { routeId } drives the rebuild.
        var routeBPoints = new List<RoutePoint>
        {
            new() { Lat = 47.0, Lng = -113.0, RecordedAt = DateTimeOffset.Parse("2025-12-23T01:31:00.000Z") },
            new() { Lat = 47.1, Lng = -113.1, RecordedAt = DateTimeOffset.Parse("2025-12-23T01:31:10.000Z") },
            new() { Lat = 47.2, Lng = -113.2, RecordedAt = DateTimeOffset.Parse("2025-12-23T01:31:20.000Z") },
            new() { Lat = 47.3, Lng = -113.3, RecordedAt = DateTimeOffset.Parse("2025-12-23T01:31:30.000Z") },
        };
        var routeB = await UploadRouteAsync("Route B", routeBPoints);
        await PatchEventAsync(eventId, $"{{\"routeId\":{routeB}}}");

        var snapB = await ReadSnapshotAsync();
        var fhB = snapB.RootElement.GetProperty("event").GetProperty("flightHistory");
        Assert.Equal(routeB, fhB.GetProperty("routeId").GetInt64());
        Assert.Equal("Route B", fhB.GetProperty("name").GetString());
        Assert.Equal(4, fhB.GetProperty("points").GetArrayLength());
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string? body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<long> CreateSponsorAsync(string name)
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/sponsors",
            $"{{\"name\":\"{name}\"}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        return dto.RootElement.GetProperty("id").GetInt64();
    }

    private async Task UpsertYearAsync(long sponsorId, int year, decimal? amount,
        bool active = true, bool canAdvertise = true, bool anonymous = false,
        int? pinnedPosition = null, int? lingerMsOverride = null)
    {
        var amt = amount is null ? "null" : amount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pin = pinnedPosition is null ? "null" : pinnedPosition.Value.ToString();
        var linger = lingerMsOverride is null ? "null" : lingerMsOverride.Value.ToString();
        var body = "{\"amountDonated\":" + amt +
            ",\"active\":" + (active ? "true" : "false") +
            ",\"canAdvertise\":" + (canAdvertise ? "true" : "false") +
            ",\"anonymous\":" + (anonymous ? "true" : "false") +
            ",\"pinnedPosition\":" + pin +
            ",\"lingerMsOverride\":" + linger + "}";
        var response = await SendAdminAsync(HttpMethod.Put, $"/admin/sponsors/{sponsorId}/years/{year}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task PutSettingAsync(string key, int value)
    {
        var body = $"{{\"value\":{value}}}";
        var response = await SendAdminAsync(HttpMethod.Put, $"/admin/settings/{key}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Bypasses the admin range check; used only where the test needs a value
    // outside the admin range (contracts 6 caps flight_history_max_points at
    // 100, but the docs example uses 3).
    private async Task SetSettingDirectAsync(string key, int value)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into app_setting (key, value, updated_by, updated_at)
values ($1, $2::jsonb, 'test', now())
on conflict (key) do update set value = excluded.value, updated_by = excluded.updated_by, updated_at = now();", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task PatchEventAsync(long eventId, string body)
    {
        var response = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{eventId}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Inserts a current event with is_current=true through SQL because
    // POST /admin/events does not set that flag; every subsequent [snapshot]
    // write picks the row up as the current event.
    private async Task<long> CreateCurrentEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var clear = new NpgsqlCommand("update event set is_current = false where is_current;", conn))
            await clear.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, 1, true, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    // Uploads a RouteObject to the object store and inserts a route row that
    // points to it. Mirrors POST /admin/routes without going through the admin
    // endpoint so the tests do not depend on the endpoint's canonicalization
    // for anything other than key derivation.
    private async Task<long> UploadRouteAsync(string name, List<RoutePoint> points)
    {
        var obj = new RouteObject { SchemaVersion = 1, Name = name, Points = points };
        var bytes = CanonicalJson.SerializeToUtf8Bytes(obj);
        var sha = CanonicalJson.Sha256Hex(bytes);
        var key = $"routes/{sha}.json";
        var url = _host!.Options.CdnBaseUrl.TrimEnd('/') + "/" + key;
        await _host.Store.PutObjectAsync(key, bytes, "application/json; charset=utf-8",
            "public, max-age=31536000, immutable");

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into route (name, s3_key, url, sha256, point_count, uploaded_by)
values ($1, $2, $3, $4, $5, 'test') returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = url });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = sha });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = points.Count });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task LinkRouteAsync(long eventId, long routeId)
    {
        await PatchEventAsync(eventId, $"{{\"routeId\":{routeId}}}");
    }

    private async Task<Guid> InsertReadyRasterMediaAsync(string filename)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into media_asset (id, filename, content_type, kind, state, s3_key, uploaded_by, alt, title, variants, width, height)
values ($1, $2, 'image/jpeg', 'raster', 'ready',
        'media/' || $1::text || '/' || $2, 'test', 'poster', '', '{}'::jsonb, 1600, 2400);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = filename });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    // Force a snapshot rebuild by writing something [snapshot] (a no-op sponsor
    // upsert on a placeholder row would create a sponsor - instead trigger a
    // settings write, which is a documented [snapshot] endpoint), then reads
    // the object PUT under snapshot.url out of the RecordingObjectStore.
    private async Task<JsonDocument> ReadSnapshotAsync()
    {
        // Any [snapshot] write rebuilds. Use a settings write with the current
        // value so the frame runs but nothing else changes; the row upsert is
        // idempotent-shaped (same value in and out).
        var body = "{\"value\":5000}";
        var response = await SendAdminAsync(HttpMethod.Put, "/admin/settings/poll_interval_ms", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var url = await ReadSnapshotUrlAsync();
        Assert.False(string.IsNullOrEmpty(url));
        var key = "snapshots/" + url.Substring(url.LastIndexOf('/') + 1);
        var content = await _host!.Store.GetObjectAsync(key);
        Assert.NotNull(content);
        return JsonDocument.Parse(content!.Bytes);
    }

    private async Task<string> ReadSnapshotUrlAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select url from snapshot where id = 1;", conn);
        return (string)(await cmd.ExecuteScalarAsync() ?? "");
    }

    private static void AssertPointEquals(RoutePoint expected, JsonElement actual)
    {
        Assert.Equal(expected.Lat, actual.GetProperty("lat").GetDouble());
        Assert.Equal(expected.Lng, actual.GetProperty("lng").GetDouble());
        Assert.Equal(expected.RecordedAt!.Value.ToUniversalTime(),
            actual.GetProperty("recordedAt").GetDateTimeOffset().ToUniversalTime());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }
}
