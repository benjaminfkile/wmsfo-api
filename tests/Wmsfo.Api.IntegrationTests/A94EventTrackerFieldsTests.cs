using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// A94: the event's tracker fields on create (sql.md 8.7), patch (8.5), and
// clone (8.4b), with the validations of contracts 4.5 Events at their paths,
// and the snapshot the patch frame rebuilds.
public sealed class A94EventTrackerFieldsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string ValleyBox = """{"west":-114.75,"south":46.35,"east":-113.30,"north":47.25}""";
    private const string InnerBox = """{"west":-114.3,"south":46.75,"east":-113.8,"north":47.05}""";
    private const string FarBox = """{"west":-110,"south":40,"east":-109,"north":41}""";

    private readonly PostgresFixture _fixture;
    private A9Host? _host;

    public A94EventTrackerFieldsTests(PostgresFixture fixture)
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
        {
            await db.Database.MigrateAsync();
        }
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (var sql in new[]
            {
                "delete from location;",
                "delete from cookie;",
                "delete from event_message;",
                "delete from event_status_history;",
                "delete from event;",
                "delete from outbox;",
                "delete from tracker_map where created_by = 'a94-test';",
                "delete from content_version where label = 'a94';",
            })
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            await SnapshotSeed.EnsureAsync(conn);
        }
        _host = await A9Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    // ---------- create ----------

    [Fact]
    public async Task Create_without_a_box_and_no_event_takes_the_constant_the_valley_map_and_every_theme()
    {
        using var dto = await CreateAsync(2026);
        AssertBox(ValleyBox, dto.RootElement.GetProperty("trackerBbox"));
        Assert.Equal(await ValleyMapIdAsync(), dto.RootElement.GetProperty("trackerMapId").GetInt64());
        Assert.Equal(await AllThemeIdsAsync(), ThemeIds(dto.RootElement));
        Assert.Equal(8, ThemeIds(dto.RootElement).Length);
    }

    [Fact]
    public async Task Create_without_a_box_after_a_publish_carrying_tracker_takes_the_published_box()
    {
        await PublishTrackerBoxAsync(InnerBox);
        using var dto = await CreateAsync(2026);
        AssertBox(InnerBox, dto.RootElement.GetProperty("trackerBbox"));
        Assert.Equal(await ValleyMapIdAsync(), dto.RootElement.GetProperty("trackerMapId").GetInt64());

        using var withNull = await CreateAsync(2027, "\"trackerBbox\":null");
        AssertBox(InnerBox, withNull.RootElement.GetProperty("trackerBbox"));
    }

    [Fact]
    public async Task Create_with_a_25_degree_box_is_400_at_trackerBbox()
    {
        var response = await SendAsync(HttpMethod.Post, "/admin/events",
            """{"year":2026,"name":"Wide","inheritRoute":true,"trackerBbox":{"west":-130,"south":30,"east":-105,"north":45}}""");
        await AssertValidationAsync(response, "trackerBbox");

        var extraKey = await SendAsync(HttpMethod.Post, "/admin/events",
            """{"year":2026,"name":"Extra","inheritRoute":true,"trackerBbox":{"west":-114,"south":46,"east":-113,"north":47,"zoom":3}}""");
        await AssertValidationAsync(extraKey, "trackerBbox");
    }

    [Fact]
    public async Task Create_copies_the_newest_events_map_and_themes_and_drops_the_map_outside_its_package()
    {
        var valley = await ValleyMapIdAsync();
        using var first = await CreateAsync(2026);
        var subset = new[] { await ThemeIdAsync("night"), await ThemeIdAsync("standard") };
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(Id(first), new JsonObject { ["trackerThemeIds"] = Ids(subset) })).StatusCode);
        var expectedOrder = await OrderedAsync(subset);

        using var inside = await CreateAsync(2027, "\"trackerBbox\":" + InnerBox);
        AssertBox(InnerBox, inside.RootElement.GetProperty("trackerBbox"));
        Assert.Equal(valley, inside.RootElement.GetProperty("trackerMapId").GetInt64());
        Assert.Equal(expectedOrder, ThemeIds(inside.RootElement));

        using var outside = await CreateAsync(2028, "\"trackerBbox\":" + FarBox);
        AssertBox(FarBox, outside.RootElement.GetProperty("trackerBbox"));
        Assert.Equal(JsonValueKind.Null, outside.RootElement.GetProperty("trackerMapId").ValueKind);
        Assert.Equal(expectedOrder, ThemeIds(outside.RootElement));

        // An older year is not the source: the 2028 event (no map) is.
        using var older = await CreateAsync(2020);
        Assert.Equal(JsonValueKind.Null, older.RootElement.GetProperty("trackerMapId").ValueKind);
    }

    [Fact]
    public async Task Create_with_every_event_deleted_enables_all_eight_and_takes_the_valley_map()
    {
        using var first = await CreateAsync(2026);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(Id(first),
            new JsonObject { ["trackerMapId"] = null, ["trackerThemeIds"] = Ids(new[] { await ThemeIdAsync("charcoal") }) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/admin/events/{Id(first)}", "")).StatusCode);

        using var fresh = await CreateAsync(2027);
        Assert.Equal(await ValleyMapIdAsync(), fresh.RootElement.GetProperty("trackerMapId").GetInt64());
        Assert.Equal(await AllThemeIdsAsync(), ThemeIds(fresh.RootElement));
    }

    // ---------- patch trackerMapId ----------

    [Fact]
    public async Task Patch_trackerMapId_checks_state_existence_and_containment()
    {
        using var ev = await CreateAsync(2026);
        var id = Id(ev);
        var pending = await InsertMapAsync("a94-pending", ValleyBox, "pending");
        var small = await InsertMapAsync("a94-small", """{"west":-114.1,"south":46.8,"east":-114.0,"north":46.9}""", "ready");

        await AssertValidationAsync(await PatchAsync(id, new JsonObject { ["trackerMapId"] = pending }), "trackerMapId");
        var unknown = await PatchAsync(id, new JsonObject { ["trackerMapId"] = 99999999 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(ApiErrorCodes.NotFound, await ReadCodeAsync(unknown));
        await AssertValidationAsync(await PatchAsync(id, new JsonObject { ["trackerMapId"] = small }), "trackerMapId");

        var cleared = await PatchAsync(id, new JsonObject { ["trackerMapId"] = null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using (var doc = await ReadJsonAsync(cleared))
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("trackerMapId").ValueKind);

        var valley = await ValleyMapIdAsync();
        var set = await PatchAsync(id, new JsonObject { ["trackerMapId"] = valley });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        using (var doc = await ReadJsonAsync(set))
            Assert.Equal(valley, doc.RootElement.GetProperty("trackerMapId").GetInt64());

        // The small map with a box inside it in the same write is accepted.
        var both = await PatchAsync(id, new JsonObject
        {
            ["trackerBbox"] = JsonNode.Parse("""{"west":-114.09,"south":46.81,"east":-114.01,"north":46.89}"""),
            ["trackerMapId"] = small,
        });
        Assert.Equal(HttpStatusCode.OK, both.StatusCode);

        var wrongType = await PatchAsync(id, new JsonObject { ["trackerMapId"] = "one" });
        await AssertValidationAsync(wrongType, "trackerMapId");
    }

    // ---------- patch trackerBbox ----------

    [Fact]
    public async Task Patch_trackerBbox_stays_inside_the_linked_map_and_is_never_null()
    {
        using var ev = await CreateAsync(2026);
        var id = Id(ev);

        await AssertValidationAsync(await PatchAsync(id, new JsonObject { ["trackerBbox"] = JsonNode.Parse(FarBox) }), "trackerBbox");

        var inside = await PatchAsync(id, new JsonObject { ["trackerBbox"] = JsonNode.Parse(InnerBox) });
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        using (var doc = await ReadJsonAsync(inside))
            AssertBox(InnerBox, doc.RootElement.GetProperty("trackerBbox"));

        await AssertValidationAsync(await PatchAsync(id, new JsonObject { ["trackerBbox"] = null }), "trackerBbox");

        // Without a map the box may go anywhere.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(id, new JsonObject { ["trackerMapId"] = null })).StatusCode);
        var far = await PatchAsync(id, new JsonObject { ["trackerBbox"] = JsonNode.Parse(FarBox) });
        Assert.Equal(HttpStatusCode.OK, far.StatusCode);
        using (var doc = await ReadJsonAsync(far))
            AssertBox(FarBox, doc.RootElement.GetProperty("trackerBbox"));
    }

    // ---------- patch trackerThemeIds ----------

    [Fact]
    public async Task Patch_trackerThemeIds_validates_then_replaces_the_rows_whole()
    {
        using var ev = await CreateAsync(2026);
        var id = Id(ev);
        var routeLight = await ThemeIdAsync("light");
        var routeDark = await ThemeIdAsync("dark");
        var standard = await ThemeIdAsync("standard");
        var nebula = await ThemeIdAsync("nebula");

        await AssertValidationAsync(await PatchAsync(id, new JsonObject { ["trackerThemeIds"] = Ids(new[] { routeLight, routeDark }) }), "trackerThemeIds");
        await AssertValidationAsync(await PatchAsync(id, new JsonObject { ["trackerThemeIds"] = Ids(new[] { standard, standard }) }), "trackerThemeIds");
        await AssertValidationAsync(await PatchAsync(id, new JsonObject { ["trackerThemeIds"] = new JsonArray() }), "trackerThemeIds");
        var unknown = await PatchAsync(id, new JsonObject { ["trackerThemeIds"] = Ids(new[] { standard, 99999999L }) });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(8, await CountAsync("select count(*) from event_tracker_theme where event_id = $1;", id));

        var ok = await PatchAsync(id, new JsonObject { ["trackerThemeIds"] = Ids(new[] { nebula, routeDark, standard }) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var doc = await ReadJsonAsync(ok);
        Assert.Equal(await OrderedAsync(new[] { nebula, routeDark, standard }), ThemeIds(doc.RootElement));
        Assert.Equal(3, await CountAsync("select count(*) from event_tracker_theme where event_id = $1;", id));

        using var read = await ReadJsonAsync(await SendAsync(HttpMethod.Get, $"/admin/events/{id}", ""));
        Assert.Equal(ThemeIds(doc.RootElement), ThemeIds(read.RootElement));
    }

    // ---------- the snapshot ----------

    [Fact]
    public async Task The_snapshot_after_a_patch_carries_the_new_box_map_and_themes()
    {
        using var ev = await CreateAsync(2026);
        var id = Id(ev);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/events/{id}/current", "")).StatusCode);
        var night = await ThemeIdAsync("night");
        var routeDark = await ThemeIdAsync("dark");
        var valley = await ValleyMapIdAsync();

        var patch = await PatchAsync(id, new JsonObject
        {
            ["trackerBbox"] = JsonNode.Parse(InnerBox),
            ["trackerMapId"] = valley,
            ["trackerThemeIds"] = Ids(new[] { night, routeDark }),
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var snap = await ReadSnapshotAsync();
        var snapEvent = snap.RootElement.GetProperty("event");
        AssertBox(InnerBox, snapEvent.GetProperty("trackerBbox"));
        Assert.Equal(valley, snapEvent.GetProperty("trackerMap").GetProperty("id").GetInt64());
        Assert.Equal(await OrderedAsync(new[] { night, routeDark }),
            snap.RootElement.GetProperty("trackerThemes").EnumerateArray().Select(t => t.GetProperty("id").GetInt64()).ToArray());

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(id, new JsonObject { ["trackerMapId"] = null })).StatusCode);
        using var cleared = await ReadSnapshotAsync();
        Assert.Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("event").GetProperty("trackerMap").ValueKind);
    }

    // ---------- clone ----------

    [Fact]
    public async Task Clone_copies_the_box_always_and_the_map_and_themes_under_tracker()
    {
        var valley = await ValleyMapIdAsync();
        using var source = await CreateAsync(2026, "\"trackerBbox\":" + InnerBox);
        var sourceId = Id(source);
        Assert.Equal(valley, source.RootElement.GetProperty("trackerMapId").GetInt64());
        var sourceThemes = ThemeIds(source.RootElement);

        // The newest event by year has no map and one theme.
        using var newest = await CreateAsync(2027);
        var charcoal = await ThemeIdAsync("charcoal");
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(Id(newest),
            new JsonObject { ["trackerMapId"] = null, ["trackerThemeIds"] = Ids(new[] { charcoal }) })).StatusCode);

        var plain = await SendAsync(HttpMethod.Post, $"/admin/events/{sourceId}/clone",
            """{"year":2028,"name":"Clone plain","copy":{"sponsors":false}}""");
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        using (var doc = await ReadJsonAsync(plain))
        {
            AssertBox(InnerBox, doc.RootElement.GetProperty("trackerBbox"));
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("trackerMapId").ValueKind);
            Assert.Equal(new[] { charcoal }, ThemeIds(doc.RootElement));
        }

        var tracker = await SendAsync(HttpMethod.Post, $"/admin/events/{sourceId}/clone",
            """{"year":2029,"name":"Clone tracker","copy":{"tracker":true}}""");
        Assert.Equal(HttpStatusCode.Created, tracker.StatusCode);
        using (var doc = await ReadJsonAsync(tracker))
        {
            AssertBox(InnerBox, doc.RootElement.GetProperty("trackerBbox"));
            Assert.Equal(valley, doc.RootElement.GetProperty("trackerMapId").GetInt64());
            Assert.Equal(sourceThemes, ThemeIds(doc.RootElement));
            Assert.Equal(sourceThemes.Length,
                await CountAsync("select count(*) from event_tracker_theme where event_id = $1;", Id(doc)));
        }
    }

    // ---------- helpers ----------

    private async Task<JsonDocument> CreateAsync(int year, string? extra = null)
    {
        var body = $"{{\"year\":{year},\"name\":\"Event {year}\",\"inheritRoute\":true{(extra is null ? "" : "," + extra)}}}";
        var response = await SendAsync(HttpMethod.Post, "/admin/events", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private Task<HttpResponseMessage> PatchAsync(long id, JsonObject body) =>
        SendAsync(HttpMethod.Patch, $"/admin/events/{id}", body.ToJsonString());

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private static async Task AssertValidationAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal(ApiErrorCodes.ValidationFailed, doc.RootElement.GetProperty("code").GetString());
        Assert.True(doc.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty(field, out _),
            $"expected a problem at {field}: {doc.RootElement.GetRawText()}");
    }

    private static void AssertBox(string expected, JsonElement actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual.GetRawText())),
            $"expected {expected}, got {actual.GetRawText()}");

    private static long Id(JsonDocument doc) => doc.RootElement.GetProperty("id").GetInt64();

    private static long[] ThemeIds(JsonElement ev) =>
        ev.GetProperty("trackerThemeIds").EnumerateArray().Select(e => e.GetInt64()).ToArray();

    private static JsonArray Ids(IEnumerable<long> ids) => new(ids.Select(i => (JsonNode?)i).ToArray());

    // A content_version whose document's settings carry tracker.defaultBbox.
    private async Task PublishTrackerBoxAsync(string box)
    {
        var doc = JsonNode.Parse(CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildContentDocument()))!;
        doc["settings"]!["tracker"] = new JsonObject { ["defaultBbox"] = JsonNode.Parse(box) };
        var json = doc.ToJsonString();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into content_version (document, sha256, media_ids, label, published_by)
values ($1::jsonb, $2, '{}'::uuid[], 'a94', 'a94-test');", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = CanonicalJson.Sha256Hex(Encoding.UTF8.GetBytes(json)) });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> InsertMapAsync(string key, string bbox, string state)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into tracker_map (name, package_key, prefix, bbox, min_zoom, max_zoom, state, created_by, updated_by)
values ($1, $1, 'maps/' || $1, $2::jsonb, 0, 14, $3, 'a94-test', 'a94-test')
returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = bbox });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = state });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> ValleyMapIdAsync() =>
        await ScalarAsync<long>("select id from tracker_map where prefix = 'basemap';");

    private async Task<long> ThemeIdAsync(string key) =>
        await ScalarAsync<long>($"select id from tracker_theme where key = '{key}';");

    private async Task<long[]> AllThemeIdsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select array(select id from tracker_theme order by sort_order, id);", conn);
        return (long[])(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long[]> OrderedAsync(long[] ids)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select array(select id from tracker_theme where id = any($1) order by sort_order, id);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = ids });
        return (long[])(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsync(string sql, long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<JsonDocument> ReadSnapshotAsync()
    {
        var key = await ScalarAsync<string>("select s3_key from snapshot where id = 1;");
        var content = await _host!.Store.GetObjectAsync(key);
        Assert.NotNull(content);
        return JsonDocument.Parse(content!.Bytes);
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());
}
