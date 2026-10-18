using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.IntegrationTests;

// One site-wide route (contracts 1.3, 1.3a settings.route):
//   - the snapshot carries routeMap and routeMapConfig from the published
//     site route whether or not an event is current;
//   - routeView and trackerThemes follow the current event, else the event
//     with the greatest year; with no event at all routeView is the published
//     default box (else the valley box) and no map, and trackerThemes is empty;
//   - event.flightHistory is built from the site route while an event is
//     current, and is null without a site route;
//   - a clone that becomes the newest event rebuilds the snapshot.
public sealed class A113SiteRouteTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string InnerBox = """{"west":-114.3,"south":46.75,"east":-113.8,"north":47.05}""";
    private const string MapConfig = """{"display":{"arrows":false,"routeWidth":"thick"},"controls":{"terrain":true}}""";

    private readonly PostgresFixture _fixture;
    private A25Host? _host;

    public A113SiteRouteTests(PostgresFixture fixture)
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
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SiteRouteSeed.ClearAsync(conn);
        await SnapshotSeed.EnsureAsync(conn);
        _host = await A25Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task With_no_current_event_the_snapshot_carries_the_site_route_and_the_newest_events_view_and_themes()
    {
        var valley = await ValleyMapIdAsync();
        var older = await CreateEventAsync(2025);
        var newest = await CreateEventAsync(2026);
        var night = await ThemeIdAsync("night");
        var dark = await ThemeIdAsync("dark");
        await PatchEventAsync(newest, new JsonObject
        {
            ["trackerBbox"] = JsonNode.Parse(InnerBox),
            ["trackerThemeIds"] = new JsonArray(night, dark),
        });

        var points = Recording(120);
        var routeId = await UploadRouteAsync("Site flight", points);
        await SiteRouteSeed.PublishAsync(_fixture.ConnectionString, SiteRouteSeed.Route(routeId, JsonNode.Parse(MapConfig)));

        using (var snap = await ReadSnapshotAsync())
        {
            var root = snap.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("event").ValueKind);
            Assert.Equal(Canonical(RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!), root.GetProperty("routeMap").GetRawText());
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(MapConfig), JsonNode.Parse(root.GetProperty("routeMapConfig").GetRawText())));
            var view = root.GetProperty("routeView");
            Assert.Equal(new[] { "bbox", "map" }, view.EnumerateObject().Select(p => p.Name).ToArray());
            AssertBox(InnerBox, view.GetProperty("bbox"));
            Assert.Equal(valley, view.GetProperty("map").GetProperty("id").GetInt64());
            Assert.Equal(await OrderedAsync(new[] { night, dark }), ThemeIds(root));
        }

        // A current event wins over the newest: its box, its map (the same
        // object as event.trackerMap), its themes, and the flight history from
        // the site route.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/events/{older}/current", null)).StatusCode);
        using (var snap = await ReadCurrentSnapshotAsync())
        {
            var root = snap.RootElement;
            var ev = root.GetProperty("event");
            Assert.Equal(older, ev.GetProperty("id").GetInt64());
            AssertBox(TrackerThemeSeed.ValleyBboxJson, root.GetProperty("routeView").GetProperty("bbox"));
            Assert.Equal(ev.GetProperty("trackerMap").GetRawText(), root.GetProperty("routeView").GetProperty("map").GetRawText());
            Assert.Equal(await AllThemeIdsAsync(), ThemeIds(root));
            Assert.Equal(routeId, ev.GetProperty("flightHistory").GetProperty("routeId").GetInt64());
            Assert.Equal("Site flight", ev.GetProperty("flightHistory").GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("routeMap").ValueKind);
        }

        // Clearing the current event goes back to the newest event.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/admin/events/{older}/current", null)).StatusCode);
        using (var snap = await ReadCurrentSnapshotAsync())
        {
            Assert.Equal(JsonValueKind.Null, snap.RootElement.GetProperty("event").ValueKind);
            AssertBox(InnerBox, snap.RootElement.GetProperty("routeView").GetProperty("bbox"));
            Assert.Equal(await OrderedAsync(new[] { night, dark }), ThemeIds(snap.RootElement));
        }

        // A theme set write on the newest event rebuilds the snapshot.
        await PatchEventAsync(newest, new JsonObject { ["trackerThemeIds"] = new JsonArray(night) });
        using (var snap = await ReadCurrentSnapshotAsync())
            Assert.Equal(new[] { night }, ThemeIds(snap.RootElement));
    }

    [Fact]
    public async Task A_clone_that_becomes_the_newest_event_rebuilds_the_route_view()
    {
        var source = await CreateEventAsync(2025);
        await PatchEventAsync(source, new JsonObject { ["trackerBbox"] = JsonNode.Parse(InnerBox) });
        var newer = await CreateEventAsync(2026);
        await PatchEventAsync(newer, new JsonObject { ["trackerMapId"] = null });
        using (var snap = await ReadCurrentSnapshotAsync())
            Assert.Equal(JsonValueKind.Null, snap.RootElement.GetProperty("routeView").GetProperty("map").ValueKind);

        var clone = await SendAsync(HttpMethod.Post, $"/admin/events/{source}/clone",
            """{"year":2027,"name":"Clone","copy":{"tracker":true}}""");
        Assert.Equal(HttpStatusCode.Created, clone.StatusCode);
        using (var snap = await ReadCurrentSnapshotAsync())
        {
            var view = snap.RootElement.GetProperty("routeView");
            AssertBox(InnerBox, view.GetProperty("bbox"));
            Assert.Equal(await ValleyMapIdAsync(), view.GetProperty("map").GetProperty("id").GetInt64());
        }
    }

    [Fact]
    public async Task With_no_events_the_route_view_is_the_default_box_without_a_map()
    {
        var points = Recording(40);
        var routeId = await UploadRouteAsync("Off season flight", points);
        await SiteRouteSeed.PublishAsync(_fixture.ConnectionString, SiteRouteSeed.Route(routeId));

        using (var snap = await ReadSnapshotAsync())
        {
            var root = snap.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("event").ValueKind);
            AssertBox(TrackerThemeSeed.ValleyBboxJson, root.GetProperty("routeView").GetProperty("bbox"));
            Assert.Equal(JsonValueKind.Null, root.GetProperty("routeView").GetProperty("map").ValueKind);
            Assert.Empty(root.GetProperty("trackerThemes").EnumerateArray());
            Assert.Equal(Canonical(RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!), root.GetProperty("routeMap").GetRawText());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("routeMapConfig").ValueKind);
        }

        // The published settings.tracker.defaultBbox wins over the valley box.
        await SiteRouteSeed.PublishAsync(_fixture.ConnectionString, SiteRouteSeed.Route(routeId),
            new JsonObject { ["defaultBbox"] = JsonNode.Parse(InnerBox) });
        using (var snap = await ReadSnapshotAsync())
        {
            AssertBox(InnerBox, snap.RootElement.GetProperty("routeView").GetProperty("bbox"));
            Assert.Equal(JsonValueKind.Null, snap.RootElement.GetProperty("routeView").GetProperty("map").ValueKind);
        }
    }

    [Fact]
    public async Task Without_a_site_route_routeMap_and_flightHistory_are_null_while_live()
    {
        var id = await CreateEventAsync(2026);
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "update event set is_current = true, status_id = 3, went_live_at = now() where id = $1;", conn);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await cmd.ExecuteNonQueryAsync();
        }

        using (var snap = await ReadSnapshotAsync())
        {
            var root = snap.RootElement;
            Assert.Equal(3, root.GetProperty("event").GetProperty("statusId").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("event").GetProperty("flightHistory").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("routeMap").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("routeMapConfig").ValueKind);
            Assert.Equal(id, root.GetProperty("event").GetProperty("id").GetInt64());
        }

        // A null routeId with a configuration is no route; the configuration
        // still rides along.
        await SiteRouteSeed.PublishAsync(_fixture.ConnectionString, SiteRouteSeed.Route(null, JsonNode.Parse(MapConfig)));
        using (var snap = await ReadSnapshotAsync())
        {
            Assert.Equal(JsonValueKind.Null, snap.RootElement.GetProperty("event").GetProperty("flightHistory").ValueKind);
            Assert.Equal(JsonValueKind.Null, snap.RootElement.GetProperty("routeMap").ValueKind);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(MapConfig),
                JsonNode.Parse(snap.RootElement.GetProperty("routeMapConfig").GetRawText())));
        }

        // A route whose object cannot be read leaves both null too.
        var unreadable = await InsertRouteRowAsync("Lost flight");
        await SiteRouteSeed.PublishAsync(_fixture.ConnectionString, SiteRouteSeed.Route(unreadable));
        using (var snap = await ReadSnapshotAsync())
        {
            Assert.Equal(JsonValueKind.Null, snap.RootElement.GetProperty("event").GetProperty("flightHistory").ValueKind);
            Assert.Equal(JsonValueKind.Null, snap.RootElement.GetProperty("routeMap").ValueKind);
        }
    }

    // ---------- helpers ----------

    private static string Canonical(RouteMap map) => Encoding.UTF8.GetString(CanonicalJson.SerializeToUtf8Bytes(map));

    private static void AssertBox(string expected, JsonElement actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual.GetRawText())),
            $"expected {expected}, got {actual.GetRawText()}");

    private static long[] ThemeIds(JsonElement snapshot) =>
        snapshot.GetProperty("trackerThemes").EnumerateArray().Select(t => t.GetProperty("id").GetInt64()).ToArray();

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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.AdminToken);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host!.Client.SendAsync(req);
    }

    private async Task<long> CreateEventAsync(int year)
    {
        var response = await SendAsync(HttpMethod.Post, "/admin/events", $"{{\"year\":{year},\"name\":\"Event {year}\"}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    private async Task PatchEventAsync(long id, JsonObject body)
    {
        var response = await SendAsync(HttpMethod.Patch, $"/admin/events/{id}", body.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<long> UploadRouteAsync(string name, List<RoutePoint> points)
    {
        var bytes = CanonicalJson.SerializeToUtf8Bytes(new RouteObject { SchemaVersion = 1, Name = name, Points = points });
        var sha = CanonicalJson.Sha256Hex(bytes);
        var key = $"routes/{sha}.json";
        await _host!.Store.PutObjectAsync(key, bytes, "application/json; charset=utf-8",
            "public, max-age=31536000, immutable");
        return await InsertRouteRowAsync(name, key, sha, points.Count);
    }

    // A route row; without a key its object is absent from the store.
    private async Task<long> InsertRouteRowAsync(string name, string? key = null, string? sha = null, int count = 2)
    {
        sha ??= new string('0', 63) + "1";
        key ??= $"routes/{sha}.json";
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into route (name, s3_key, url, sha256, point_count, uploaded_by)
values ($1, $2, 'https://cdn.example/' || $2, $3, $4, 'test') returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = sha });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = count });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> ValleyMapIdAsync() =>
        await ScalarAsync<long>("select id from tracker_map where prefix = 'basemap';");

    private async Task<long> ThemeIdAsync(string key) =>
        await ScalarAsync<long>($"select id from tracker_theme where key = '{key}';");

    private async Task<long[]> AllThemeIdsAsync() =>
        await ScalarAsync<long[]>("select array(select id from tracker_theme order by sort_order, id);");

    private async Task<long[]> OrderedAsync(long[] ids)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select array(select id from tracker_theme where id = any($1) order by sort_order, id);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = ids });
        return (long[])(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    // Runs a [snapshot] write that changes nothing, then reads the snapshot.
    private async Task<JsonDocument> ReadSnapshotAsync()
    {
        var response = await SendAsync(HttpMethod.Put, "/admin/settings/poll_interval_ms", "{\"value\":5000}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadCurrentSnapshotAsync();
    }

    // Reads the object the snapshot row points at, without a rebuild.
    private async Task<JsonDocument> ReadCurrentSnapshotAsync()
    {
        var key = await ScalarAsync<string>("select s3_key from snapshot where id = 1;");
        var content = await _host!.Store.GetObjectAsync(key);
        Assert.NotNull(content);
        return JsonDocument.Parse(content!.Bytes);
    }
}
