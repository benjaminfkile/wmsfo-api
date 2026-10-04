using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// event.routeMapConfig (contracts 1.3, 4.5 Events):
//   - a whole config and each key alone round-trip through PATCH and GET
//   - display.labelSize round-trips last in display and stays absent when unset
//   - absent leaves it, null clears it, a new event has null
//   - bad enums, a bad kind token, unknown keys (`landmarks` among them, a
//     site setting), and a non-object are 400 on the field; the row is unchanged
//   - the PATCH rebuilds the snapshot, whose current event block carries the
//     config (null without one)
//   - clone copies the config behind copy.routeMapConfig only
public sealed class A67RouteMapConfigTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    // Every key set, in contract order, as the API answers and publishes it.
    private const string FullConfig =
        "{\"display\":{\"timeLabelIntervalMinutes\":0,\"arrows\":false,\"arrowSize\":\"xlarge\",\"routeWidth\":\"xthick\",\"labelSize\":\"large\"},"
        + "\"controls\":{\"fullscreen\":false,\"terrain\":false},"
        + "\"pois\":{\"kinds\":[\"school\",\"place_of_worship\"]}}";

    private readonly PostgresFixture _fixture;
    private A9Host? _host;

    public A67RouteMapConfigTests(PostgresFixture fixture)
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
            "delete from location;",
            "delete from cookie;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from event;",
            "delete from outbox;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
        _host = await A9Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Whole_config_round_trips_through_patch_get_and_list()
    {
        var id = await CreateEventAsync(2301);

        var patch = await PatchAsync(id, "{\"routeMapConfig\":" + FullConfig + "}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using (var patched = await ReadJsonAsync(patch))
        {
            Assert.Equal(FullConfig, patched.RootElement.GetProperty("routeMapConfig").GetRawText());
        }

        using (var got = await GetEventAsync(id))
        {
            Assert.Equal(FullConfig, got.RootElement.GetProperty("routeMapConfig").GetRawText());
        }

        using var list = await ReadJsonAsync(await SendAdminAsync(HttpMethod.Get, "/admin/events", ""));
        var row = list.RootElement.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("id").GetInt64() == id);
        Assert.Equal(FullConfig, row.GetProperty("routeMapConfig").GetRawText());
    }

    // Each key alone round-trips as written, and the other keys stay absent.
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"display\":{}}")]
    [InlineData("{\"display\":{\"timeLabelIntervalMinutes\":5}}")]
    [InlineData("{\"display\":{\"arrows\":true}}")]
    [InlineData("{\"display\":{\"arrowSize\":\"small\"}}")]
    [InlineData("{\"display\":{\"routeWidth\":\"thin\"}}")]
    [InlineData("{\"display\":{\"labelSize\":\"small\"}}")]
    [InlineData("{\"display\":{\"labelSize\":\"medium\"}}")]
    [InlineData("{\"display\":{\"labelSize\":\"large\"}}")]
    [InlineData("{\"controls\":{}}")]
    [InlineData("{\"controls\":{\"fullscreen\":false}}")]
    [InlineData("{\"controls\":{\"terrain\":true}}")]
    [InlineData("{\"pois\":{\"kinds\":[]}}")]
    [InlineData("{\"pois\":{\"kinds\":[\"hospital\"]}}")]
    public async Task Each_key_round_trips_alone(string config)
    {
        var id = await CreateEventAsync(2302);

        var patch = await PatchAsync(id, "{\"routeMapConfig\":" + config + "}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using (var patched = await ReadJsonAsync(patch))
        {
            Assert.Equal(config, patched.RootElement.GetProperty("routeMapConfig").GetRawText());
        }
        using var got = await GetEventAsync(id);
        Assert.Equal(config, got.RootElement.GetProperty("routeMapConfig").GetRawText());
    }

    // labelSize sits last in display, and a display without it stores and
    // answers no labelSize key at all.
    [Fact]
    public async Task Label_size_round_trips_in_display_and_absent_stays_absent()
    {
        var id = await CreateEventAsync(2311);
        var patch = await PatchAsync(id,
            "{\"routeMapConfig\":{\"display\":{\"labelSize\":\"small\",\"arrows\":false}}}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using (var got = await GetEventAsync(id))
        {
            Assert.Equal("{\"display\":{\"arrows\":false,\"labelSize\":\"small\"}}",
                got.RootElement.GetProperty("routeMapConfig").GetRawText());
        }

        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(id, "{\"routeMapConfig\":{\"display\":{\"arrows\":false}}}")).StatusCode);
        using (var without = await GetEventAsync(id))
        {
            var display = without.RootElement.GetProperty("routeMapConfig").GetProperty("display");
            Assert.False(display.TryGetProperty("labelSize", out _));
        }
        Assert.DoesNotContain("labelSize", await ReadStoredConfigAsync(id), StringComparison.Ordinal);
    }

    // Keys written out of contract order come back in contract order.
    [Fact]
    public async Task Config_is_stored_in_contract_order()
    {
        var id = await CreateEventAsync(2303);
        var patch = await PatchAsync(id,
            "{\"routeMapConfig\":{\"pois\":{\"kinds\":[\"park\"]},\"display\":{\"routeWidth\":\"thick\",\"arrows\":true}}}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var got = await GetEventAsync(id);
        Assert.Equal("{\"display\":{\"arrows\":true,\"routeWidth\":\"thick\"},\"pois\":{\"kinds\":[\"park\"]}}",
            got.RootElement.GetProperty("routeMapConfig").GetRawText());
    }

    [Fact]
    public async Task New_event_has_null_config_absent_leaves_it_and_null_clears_it()
    {
        var id = await CreateEventAsync(2304);
        using (var fresh = await GetEventAsync(id))
        {
            Assert.Equal(JsonValueKind.Null, fresh.RootElement.GetProperty("routeMapConfig").ValueKind);
        }

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(id, "{\"routeMapConfig\":" + FullConfig + "}")).StatusCode);

        var rename = await PatchAsync(id, "{\"name\":\"Renamed\"}");
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        using (var kept = await GetEventAsync(id))
        {
            Assert.Equal("Renamed", kept.RootElement.GetProperty("name").GetString());
            Assert.Equal(FullConfig, kept.RootElement.GetProperty("routeMapConfig").GetRawText());
        }

        var clear = await PatchAsync(id, "{\"routeMapConfig\":null}");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using (var cleared = await GetEventAsync(id))
        {
            Assert.Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("routeMapConfig").ValueKind);
        }
        Assert.Null(await ReadStoredConfigAsync(id));
    }

    [Theory]
    [InlineData("{\"display\":{\"timeLabelIntervalMinutes\":20}}", "routeMapConfig.display.timeLabelIntervalMinutes")]
    [InlineData("{\"display\":{\"arrows\":\"yes\"}}", "routeMapConfig.display.arrows")]
    [InlineData("{\"display\":{\"arrowSize\":\"huge\"}}", "routeMapConfig.display.arrowSize")]
    [InlineData("{\"display\":{\"routeWidth\":\"wide\"}}", "routeMapConfig.display.routeWidth")]
    [InlineData("{\"display\":{\"labelSize\":\"xlarge\"}}", "routeMapConfig.display.labelSize")]
    [InlineData("{\"display\":{\"labelSize\":2}}", "routeMapConfig.display.labelSize")]
    [InlineData("{\"controls\":{\"terrain\":1}}", "routeMapConfig.controls.terrain")]
    [InlineData("{\"pois\":{\"kinds\":[\"school\",\"Bad-Kind\"]}}", "routeMapConfig.pois.kinds[1]")]
    [InlineData("{\"pois\":{}}", "routeMapConfig.pois")]
    [InlineData("{\"color\":\"red\"}", "routeMapConfig.color")]
    [InlineData("{\"display\":{\"color\":\"red\"}}", "routeMapConfig.display.color")]
    [InlineData("{\"controls\":{\"satellite\":true}}", "routeMapConfig.controls.satellite")]
    [InlineData("{\"pois\":{\"kinds\":[],\"zoom\":14}}", "routeMapConfig.pois.zoom")]
    [InlineData("{\"landmarks\":[]}", "routeMapConfig.landmarks")]
    [InlineData("{\"landmarks\":[{\"name\":\"Courthouse\",\"lat\":46.87,\"lng\":-113.99}]}", "routeMapConfig.landmarks")]
    [InlineData("[]", "routeMapConfig")]
    [InlineData("\"config\"", "routeMapConfig")]
    public async Task Bad_config_is_400_on_the_field_and_changes_nothing(string config, string field)
    {
        var id = await CreateEventAsync(2305);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(id, "{\"routeMapConfig\":{\"controls\":{}}}")).StatusCode);

        var response = await PatchAsync(id, "{\"routeMapConfig\":" + config + "}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var doc = await ReadJsonAsync(response))
        {
            Assert.Equal(ApiErrorCodes.ValidationFailed, doc.RootElement.GetProperty("code").GetString());
            var fields = doc.RootElement.GetProperty("details").GetProperty("fields");
            Assert.True(fields.TryGetProperty(field, out _), $"no problem on {field}: {fields}");
        }
        Assert.Equal("{\"controls\": {}}", await ReadStoredConfigAsync(id));
    }

    // The PATCH is snapshot-affecting: the rebuilt snapshot's current event
    // block carries the config beside routeMap and null once it is cleared.
    [Fact]
    public async Task Patch_rebuilds_the_snapshot_and_the_current_event_block_carries_the_config()
    {
        var id = await CreateEventAsync(2308);
        await RunSqlAsync($"update event set is_current = false; update event set is_current = true where id = {id};");

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(id, "{\"name\":\"Map Flyover\"}")).StatusCode);
        var versionBefore = await ReadSnapshotVersionAsync();
        using (var before = await ReadSnapshotAsync())
        {
            var ev = before.RootElement.GetProperty("event");
            Assert.Equal(JsonValueKind.Null, ev.GetProperty("routeMapConfig").ValueKind);
            AssertRouteMapConfigFollowsRouteMap(ev);
        }

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(id, "{\"routeMapConfig\":" + FullConfig + "}")).StatusCode);
        Assert.True(await ReadSnapshotVersionAsync() > versionBefore);
        using (var set = await ReadSnapshotAsync())
        {
            var ev = set.RootElement.GetProperty("event");
            Assert.Equal(FullConfig, ev.GetProperty("routeMapConfig").GetRawText());
            AssertRouteMapConfigFollowsRouteMap(ev);
        }

        var versionBeforeClear = await ReadSnapshotVersionAsync();
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(id, "{\"routeMapConfig\":null}")).StatusCode);
        Assert.True(await ReadSnapshotVersionAsync() > versionBeforeClear);
        using (var cleared = await ReadSnapshotAsync())
        {
            Assert.Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("event").GetProperty("routeMapConfig").ValueKind);
        }
    }

    // Only the current event's config reaches the snapshot.
    [Fact]
    public async Task Snapshot_carries_only_the_current_events_config()
    {
        var current = await CreateEventAsync(2309);
        var other = await CreateEventAsync(2310);
        await RunSqlAsync($"update event set is_current = false; update event set is_current = true where id = {current};");

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(other, "{\"routeMapConfig\":" + FullConfig + "}")).StatusCode);
        using var snapshot = await ReadSnapshotAsync();
        var ev = snapshot.RootElement.GetProperty("event");
        Assert.Equal(current, ev.GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, ev.GetProperty("routeMapConfig").ValueKind);
    }

    [Fact]
    public async Task Clone_copies_the_config_only_behind_its_flag()
    {
        var source = await CreateEventAsync(2090);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(source, "{\"routeMapConfig\":" + FullConfig + "}")).StatusCode);

        var copied = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{source}/clone",
            "{\"year\":2091,\"name\":\"Copied\",\"copy\":{\"routeMapConfig\":true}}");
        Assert.Equal(HttpStatusCode.Created, copied.StatusCode);
        using (var doc = await ReadJsonAsync(copied))
        {
            Assert.Equal(FullConfig, doc.RootElement.GetProperty("routeMapConfig").GetRawText());
            using var got = await GetEventAsync(doc.RootElement.GetProperty("id").GetInt64());
            Assert.Equal(FullConfig, got.RootElement.GetProperty("routeMapConfig").GetRawText());
        }

        var bare = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{source}/clone",
            "{\"year\":2092,\"name\":\"Bare\",\"copy\":{\"route\":true,\"poster\":true}}");
        Assert.Equal(HttpStatusCode.Created, bare.StatusCode);
        using (var doc = await ReadJsonAsync(bare))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("routeMapConfig").ValueKind);
        }

        var noCopy = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{source}/clone",
            "{\"year\":2093,\"name\":\"No copy\"}");
        Assert.Equal(HttpStatusCode.Created, noCopy.StatusCode);
        using (var doc = await ReadJsonAsync(noCopy))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("routeMapConfig").ValueKind);
        }
    }

    private static void AssertRouteMapConfigFollowsRouteMap(JsonElement ev)
    {
        var keys = ev.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(keys.IndexOf("routeMap") + 1, keys.IndexOf("routeMapConfig"));
    }

    private Task<HttpResponseMessage> PatchAsync(long id, string body) =>
        SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", body);

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<JsonDocument> GetEventAsync(long id)
    {
        var response = await SendAdminAsync(HttpMethod.Get, $"/admin/events/{id}", "");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<long> CreateEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, created_by, updated_at)
values ($1, $2, 1, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<string?> ReadStoredConfigAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select route_map_config::text from event where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        var value = await cmd.ExecuteScalarAsync();
        return value is string s ? s : null;
    }

    private async Task<long> ReadSnapshotVersionAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select version from snapshot where id = 1;", conn);
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    // The object the snapshot row points at.
    private async Task<JsonDocument> ReadSnapshotAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select s3_key from snapshot where id = 1;", conn);
        var key = (string)(await cmd.ExecuteScalarAsync() ?? "");
        var content = await _host!.Store.GetObjectAsync(key);
        Assert.NotNull(content);
        return JsonDocument.Parse(content!.Bytes);
    }

    private async Task RunSqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());
}
