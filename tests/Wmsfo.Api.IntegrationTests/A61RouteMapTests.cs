using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// Route map acceptance (contracts 1.3 event.routeMap, 4.5 Events, 6):
//   - the snapshot of a current event with a linked recording carries routeMap
//     in the documented shape; without a recording it is null.
//   - GET /admin/events/{id}/route-map answers for a non-current event with the
//     same object the builder makes, for admin and editor, and 404s an unknown id.
//   - the three route_map_* settings list with their defaults, round-trip, and
//     reject out-of-range writes.
public sealed class A61RouteMapTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly string[] SettingKeys =
    {
        RouteMapSettings.SimplifyToleranceKey, RouteMapSettings.MaxPointsKey, RouteMapSettings.DefaultDurationKey,
    };

    private readonly PostgresFixture _fixture;
    private A25Host? _host;

    public A61RouteMapTests(PostgresFixture fixture)
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
            "delete from route;",
            "delete from app_setting where key like 'route_map_%';",
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
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("delete from app_setting where key like 'route_map_%';", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Snapshot_carries_routeMap_for_a_linked_recording_and_null_without_one()
    {
        var eventId = await CreateEventAsync(2031, current: true);

        var before = await ReadSnapshotAsync();
        var ev = before.RootElement.GetProperty("event");
        Assert.Equal(JsonValueKind.Null, ev.GetProperty("routeMap").ValueKind);
        Assert.Equal(
            new[] { "id", "year", "name", "statusId", "scheduledAt", "wentLiveAt", "endedAt", "fundsPercent",
                    "routeImageMediaId", "flightHistory", "routeMap", "latestMessage" },
            ev.EnumerateObject().Select(p => p.Name).ToArray());

        var points = Recording(241); // 60 minutes, one point every 15 seconds
        var routeId = await UploadRouteAsync("Map flight", points);
        await PatchEventAsync(eventId, $"{{\"routeId\":{routeId}}}");

        var after = await ReadSnapshotAsync();
        var map = after.RootElement.GetProperty("event").GetProperty("routeMap");
        Assert.Equal(new[] { "path", "timeline", "durationMinutes", "timed" }, map.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(map.GetProperty("timed").GetBoolean());
        Assert.Equal(60, map.GetProperty("durationMinutes").GetInt32());
        var path = map.GetProperty("path").EnumerateArray().ToArray();
        Assert.InRange(path.Length, 2, RouteMapSettings.DefaultMaxPoints);
        Assert.Equal(new[] { "lat", "lng" }, path[0].EnumerateObject().Select(p => p.Name).ToArray());
        var timeline = map.GetProperty("timeline").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(0, 13).Select(i => i * 5).ToArray(), timeline.Select(t => t.GetProperty("minutes").GetInt32()).ToArray());
        Assert.Equal(new[] { "minutes", "lat", "lng" }, timeline[0].EnumerateObject().Select(p => p.Name).ToArray());

        // The embed is exactly what the builder makes from the linked recording.
        var expected = RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!;
        Assert.Equal(Encoding.UTF8.GetString(CanonicalJson.SerializeToUtf8Bytes(expected)), map.GetRawText());

        // The flight history is unchanged beside it.
        Assert.Equal(routeId, after.RootElement.GetProperty("event").GetProperty("flightHistory").GetProperty("routeId").GetInt64());

        // A route_map_* setting write is snapshot-affecting.
        await PutSettingAsync(RouteMapSettings.MaxPointsKey, 100);
        var capped = await ReadSnapshotAsync();
        Assert.Equal(100, capped.RootElement.GetProperty("event").GetProperty("routeMap").GetProperty("path").GetArrayLength());
    }

    [Fact]
    public async Task Route_map_endpoint_answers_for_any_event_and_404s_an_unknown_id()
    {
        await CreateEventAsync(2032, current: true);
        var other = await CreateEventAsync(2033, current: false);

        var none = await SendAsync(HttpMethod.Get, $"/admin/events/{other}/route-map", null, DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        using (var doc = JsonDocument.Parse(await none.Content.ReadAsStringAsync()))
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("routeMap").ValueKind);

        var points = Recording(100);
        foreach (var p in points) p.RecordedAt = null; // untimed
        var routeId = await UploadRouteAsync("Other flight", points);
        await PatchEventAsync(other, $"{{\"routeId\":{routeId}}}");

        var expected = Encoding.UTF8.GetString(CanonicalJson.SerializeToUtf8Bytes(
            RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!));
        foreach (var token in new[] { DevStaticTokens.AdminToken, DevStaticTokens.EditorToken })
        {
            var response = await SendAsync(HttpMethod.Get, $"/admin/events/{other}/route-map", null, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var map = doc.RootElement.GetProperty("routeMap");
            Assert.False(map.GetProperty("timed").GetBoolean());
            Assert.Equal(RouteMapSettings.DefaultDefaultDurationMinutes, map.GetProperty("durationMinutes").GetInt32());
            Assert.Equal(expected, JsonSerializer.Serialize(map));
        }

        var person = await SendAsync(HttpMethod.Get, $"/admin/events/{other}/route-map", null, DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.Forbidden, person.StatusCode);

        var missing = await SendAsync(HttpMethod.Get, "/admin/events/987654321/route-map", null, DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Route_map_settings_round_trip_and_reject_out_of_range_writes()
    {
        var defaults = await ReadSettingsAsync();
        Assert.Equal(30, defaults[RouteMapSettings.SimplifyToleranceKey]);
        Assert.Equal(1200, defaults[RouteMapSettings.MaxPointsKey]);
        Assert.Equal(120, defaults[RouteMapSettings.DefaultDurationKey]);

        var ranges = new (string Key, int Low, int High, int Ok)[]
        {
            (RouteMapSettings.SimplifyToleranceKey, 1, 500, 45),
            (RouteMapSettings.MaxPointsKey, 100, 10000, 2500),
            (RouteMapSettings.DefaultDurationKey, 10, 720, 90),
        };
        foreach (var (key, low, high, ok) in ranges)
        {
            foreach (var bad in new[] { low - 1, high + 1 })
            {
                var response = await SendAsync(HttpMethod.Put, $"/admin/settings/{key}", $"{{\"value\":{bad}}}", DevStaticTokens.AdminToken);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            var fraction = await SendAsync(HttpMethod.Put, $"/admin/settings/{key}", $"{{\"value\":{ok}.5}}", DevStaticTokens.AdminToken);
            Assert.Equal(HttpStatusCode.BadRequest, fraction.StatusCode);
            await PutSettingAsync(key, low);
            await PutSettingAsync(key, high);
            await PutSettingAsync(key, ok);
        }

        var after = await ReadSettingsAsync();
        Assert.Equal(45, after[RouteMapSettings.SimplifyToleranceKey]);
        Assert.Equal(2500, after[RouteMapSettings.MaxPointsKey]);
        Assert.Equal(90, after[RouteMapSettings.DefaultDurationKey]);
    }

    // ---------- helpers ----------

    // A curving recording north-east of the valley, one point every 15 seconds.
    private static List<RoutePoint> Recording(int count)
    {
        var start = DateTimeOffset.Parse("2025-12-22T01:00:00.000Z");
        return Enumerable.Range(0, count)
            .Select(i => new RoutePoint
            {
                Lat = 46.87 + 0.01 * Math.Sin(i * 0.05) + i * 0.0002,
                Lng = -114.0 + 0.01 * Math.Cos(i * 0.05),
                RecordedAt = start.AddSeconds(i * 15),
            })
            .ToList();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, string token)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host!.Client.SendAsync(req);
    }

    private async Task PutSettingAsync(string key, int value)
    {
        var response = await SendAsync(HttpMethod.Put, $"/admin/settings/{key}", $"{{\"value\":{value}}}", DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Dictionary<string, int>> ReadSettingsAsync()
    {
        var response = await SendAsync(HttpMethod.Get, "/admin/settings", null, DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => SettingKeys.Contains(i.GetProperty("key").GetString()))
            .ToDictionary(i => i.GetProperty("key").GetString()!, i => i.GetProperty("value").GetInt32());
    }

    private async Task PatchEventAsync(long eventId, string body)
    {
        var response = await SendAsync(HttpMethod.Patch, $"/admin/events/{eventId}", body, DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<long> CreateEventAsync(int year, bool current)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        if (current)
        {
            await using var clear = new NpgsqlCommand("update event set is_current = false where is_current;", conn);
            await clear.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, 1, $3, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = current });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    // Stores a RouteObject and its route row the way POST /admin/routes does.
    private async Task<long> UploadRouteAsync(string name, List<RoutePoint> points)
    {
        var bytes = CanonicalJson.SerializeToUtf8Bytes(new RouteObject { SchemaVersion = 1, Name = name, Points = points });
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

    // Runs a [snapshot] write that changes nothing, then reads the object the
    // snapshot row points at.
    private async Task<JsonDocument> ReadSnapshotAsync()
    {
        await PutSettingAsync("poll_interval_ms", 5000);
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select s3_key from snapshot where id = 1;", conn);
        var key = (string)(await cmd.ExecuteScalarAsync() ?? "");
        var content = await _host!.Store.GetObjectAsync(key);
        Assert.NotNull(content);
        return JsonDocument.Parse(content!.Bytes);
    }
}
