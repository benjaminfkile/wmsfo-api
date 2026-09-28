using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// Posters (contracts 4.5 Posters, Routes, Events):
//   - poster CRUD round-trips; the layout comes back byte-equal to its
//     canonical form; null clears, absent leaves; the 32 KB and object rules
//   - the list is newest first and carries id, name, routeId, updatedAt
//   - the poster delete impact names the poster
//   - deleting a linked route nulls poster.route_id and lists the poster
//     under unlinks, in the preview and in the delete's audit row
//   - GET /admin/routes/{id}/route-map answers for any recording and 404s
//   - an event PATCH carrying posterLayout is 400; the Event has no posterLayout
public sealed class A64PosterTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Sentinel = "posterSentinel64";

    private readonly PostgresFixture _fixture;
    private A25Host? _host;

    public A64PosterTests(PostgresFixture fixture)
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
            "delete from poster;",
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
        await SnapshotSeed.EnsureAsync(conn);
        _host = await A25Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private static string SampleLayout() =>
        "{\"version\":1,\"canvas\":{\"height\":1800,\"width\":1200},\"items\":[" +
        "{\"id\":\"title\",\"kind\":\"text\",\"text\":\"" + Sentinel + " \\u00e9 <b>\",\"x\":0.5,\"y\":-12}," +
        "{\"id\":\"qr\",\"kind\":\"qr\",\"tag\":\"qr-007\",\"scale\":1.25,\"visible\":true,\"z\":null}]}";

    private static byte[] Canonical(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return CanonicalJson.SerializeOpaqueToUtf8Bytes(doc.RootElement);
    }

    [Fact]
    public async Task Poster_crud_round_trips_with_the_layout_byte_equal()
    {
        var routeId = await UploadRouteAsync("Poster flight", Recording(20));
        var layout = SampleLayout();
        var expected = Canonical(layout);

        var create = await SendAsync(HttpMethod.Post, "/admin/posters",
            $"{{\"name\":\"  Main street  \",\"routeId\":{routeId},\"layout\":{layout}}}");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        long id;
        using (var created = await ReadJsonAsync(create))
        {
            var root = created.RootElement;
            id = root.GetProperty("id").GetInt64();
            Assert.Equal("Main street", root.GetProperty("name").GetString());
            Assert.Equal(routeId, root.GetProperty("routeId").GetInt64());
            Assert.Equal(expected, CanonicalJson.SerializeToUtf8Bytes(root.GetProperty("layout")));
            Assert.Equal("create", root.GetProperty("audit").GetProperty("action").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("createdBy").GetString()));
        }

        using (var got = await GetPosterAsync(id))
        {
            Assert.Equal(expected, CanonicalJson.SerializeToUtf8Bytes(got.RootElement.GetProperty("layout")));
            Assert.Equal(routeId, got.RootElement.GetProperty("routeId").GetInt64());
        }

        // Properties come back in ordinal order at every depth.
        var reordered = await SendAsync(HttpMethod.Patch, $"/admin/posters/{id}",
            "{\"layout\":{\"zeta\":1,\"alpha\":{\"b\":[{\"y\":2,\"x\":1}],\"a\":\"s\"}}}");
        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
        using (var got = await GetPosterAsync(id))
        {
            Assert.Equal("{\"alpha\":{\"a\":\"s\",\"b\":[{\"x\":1,\"y\":2}]},\"zeta\":1}",
                got.RootElement.GetProperty("layout").GetRawText());
        }

        // Absent fields are left alone.
        var rename = await SendAsync(HttpMethod.Patch, $"/admin/posters/{id}", "{\"name\":\"Renamed\"}");
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        using (var got = await GetPosterAsync(id))
        {
            Assert.Equal("Renamed", got.RootElement.GetProperty("name").GetString());
            Assert.Equal(routeId, got.RootElement.GetProperty("routeId").GetInt64());
            Assert.Equal(JsonValueKind.Object, got.RootElement.GetProperty("layout").ValueKind);
            Assert.Equal("update", got.RootElement.GetProperty("audit").GetProperty("action").GetString());
        }

        // An unknown route is 404 and changes nothing.
        var unknownRoute = await SendAsync(HttpMethod.Patch, $"/admin/posters/{id}", "{\"routeId\":987654321}");
        Assert.Equal(HttpStatusCode.NotFound, unknownRoute.StatusCode);

        // Null clears both.
        var clear = await SendAsync(HttpMethod.Patch, $"/admin/posters/{id}", "{\"routeId\":null,\"layout\":null}");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using (var got = await GetPosterAsync(id))
        {
            Assert.Equal(JsonValueKind.Null, got.RootElement.GetProperty("routeId").ValueKind);
            Assert.Equal(JsonValueKind.Null, got.RootElement.GetProperty("layout").ValueKind);
        }
        Assert.True(await ScalarAsync<bool>($"select layout is null and route_id is null from poster where id = {id};"));

        // A route id sets it again.
        var relink = await SendAsync(HttpMethod.Patch, $"/admin/posters/{id}", $"{{\"routeId\":{routeId}}}");
        Assert.Equal(HttpStatusCode.OK, relink.StatusCode);

        // The editor group reaches every poster endpoint; a person does not.
        using (var editorGet = await SendAsync(HttpMethod.Get, $"/admin/posters/{id}", null, DevStaticTokens.EditorToken))
            Assert.Equal(HttpStatusCode.OK, editorGet.StatusCode);
        using (var personGet = await SendAsync(HttpMethod.Get, $"/admin/posters/{id}", null, DevStaticTokens.PersonToken))
            Assert.Equal(HttpStatusCode.Forbidden, personGet.StatusCode);

        var delete = await SendAsync(HttpMethod.Delete, $"/admin/posters/{id}", null, DevStaticTokens.EditorToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/posters/{id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/admin/posters/{id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Patch, $"/admin/posters/{id}", "{\"name\":\"x\"}")).StatusCode);

        // Every write is audited; the delete's before carries the impact.
        var actions = await AuditActionsAsync(id);
        Assert.Equal(new[] { "create", "update", "update", "update", "update", "delete" }, actions);
        var deleteBefore = await ScalarAsync<string>(
            $"select before::text from audit_log where entity = 'poster' and entity_id = '{id}' and action = 'delete';");
        Assert.Contains("\"impact\"", deleteBefore, StringComparison.Ordinal);
        Assert.Contains("Renamed", deleteBefore, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_defaults_and_validation()
    {
        var bare = await SendAsync(HttpMethod.Post, "/admin/posters", "{\"name\":\"Bare\"}");
        Assert.Equal(HttpStatusCode.Created, bare.StatusCode);
        using (var doc = await ReadJsonAsync(bare))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("routeId").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("layout").ValueKind);
        }

        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Post, "/admin/posters", "{\"name\":\"\"}"), "name");
        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Post, "/admin/posters",
            "{\"name\":\"" + new string('n', 201) + "\"}"), "name");
        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Post, "/admin/posters",
            "{\"name\":\"x\",\"routeId\":\"7\"}"), "routeId");
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Post, "/admin/posters", "{\"name\":\"x\",\"routeId\":987654321}")).StatusCode);

        // Unknown fields are refused by the strict DTOs.
        var unknown = await SendAsync(HttpMethod.Post, "/admin/posters", "{\"name\":\"x\",\"eventId\":1}");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from poster;"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"a\":1}]")]
    [InlineData("\"layout\"")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task Non_object_layout_is_400_on_layout(string value)
    {
        await AssertValidationFailedOnAsync(
            await SendAsync(HttpMethod.Post, "/admin/posters", "{\"name\":\"x\",\"layout\":" + value + "}"), "layout");

        var id = await CreatePosterAsync("Cap");
        await AssertValidationFailedOnAsync(
            await SendAsync(HttpMethod.Patch, $"/admin/posters/{id}", "{\"layout\":" + value + "}"), "layout");
        Assert.True(await ScalarAsync<bool>($"select layout is null from poster where id = {id};"));
    }

    [Fact]
    public async Task Layout_over_32_kb_canonical_is_400_and_exactly_32_kb_is_accepted()
    {
        // {"p":"<n characters>"} is n + 8 bytes canonical.
        static string LayoutOfSize(int bytes) => "{\"p\":\"" + new string('x', bytes - 8) + "\"}";

        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Post, "/admin/posters",
            "{\"name\":\"x\",\"layout\":" + LayoutOfSize(32 * 1024 + 1) + "}"), "layout");

        var id = await CreatePosterAsync("Cap");
        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Patch, $"/admin/posters/{id}",
            "{\"layout\":" + LayoutOfSize(32 * 1024 + 1) + "}"), "layout");
        Assert.True(await ScalarAsync<bool>($"select layout is null from poster where id = {id};"));

        var atCap = await SendAsync(HttpMethod.Patch, $"/admin/posters/{id}", "{\"layout\":" + LayoutOfSize(32 * 1024) + "}");
        Assert.Equal(HttpStatusCode.OK, atCap.StatusCode);

        // Whitespace in the request does not count; the canonical form does.
        var padded = "{ \"p\" : \"" + new string('x', 32 * 1024 - 8) + "\"" + new string(' ', 4000) + "}";
        var paddedResponse = await SendAsync(HttpMethod.Patch, $"/admin/posters/{id}", "{\"layout\":" + padded + "}");
        Assert.Equal(HttpStatusCode.OK, paddedResponse.StatusCode);

        var created = await SendAsync(HttpMethod.Post, "/admin/posters", "{\"name\":\"y\",\"layout\":" + padded + "}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task List_is_newest_first_with_the_summary_fields()
    {
        var routeId = await UploadRouteAsync("List flight", Recording(10));
        var first = await CreatePosterAsync("First");
        var second = await CreatePosterAsync("Second");
        var third = await CreatePosterAsync("Third");
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Patch, $"/admin/posters/{second}", $"{{\"routeId\":{routeId},\"layout\":{{\"a\":1}}}}")).StatusCode);

        var response = await SendAsync(HttpMethod.Get, "/admin/posters", null, DevStaticTokens.EditorToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { third, second, first }, items.Select(i => i.GetProperty("id").GetInt64()).ToArray());
        Assert.Equal(new[] { "Third", "Second", "First" }, items.Select(i => i.GetProperty("name").GetString()).ToArray());
        Assert.Equal(routeId, items[1].GetProperty("routeId").GetInt64());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("routeId").ValueKind);
        foreach (var item in items)
        {
            Assert.Equal(new[] { "id", "name", "routeId", "updatedAt" },
                item.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public async Task Poster_impact_names_the_poster()
    {
        var id = await CreatePosterAsync("Impact poster");
        var response = await SendAsync(HttpMethod.Get, $"/admin/posters/{id}/impact", null, DevStaticTokens.EditorToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var doc = await ReadJsonAsync(response))
        {
            var root = doc.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("blocked").ValueKind);
            var group = Assert.Single(root.GetProperty("deletes").EnumerateArray());
            Assert.Equal("poster", group.GetProperty("entity").GetString());
            Assert.Equal(1, group.GetProperty("count").GetInt32());
            Assert.Equal("Impact poster", Assert.Single(group.GetProperty("names").EnumerateArray()).GetString());
            Assert.Empty(root.GetProperty("unlinks").EnumerateArray());
            Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        }

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, "/admin/posters/987654321/impact", null)).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_linked_route_unlinks_its_posters_and_lists_them()
    {
        var routeId = await UploadRouteAsync("Doomed flight", Recording(10));
        var otherRouteId = await UploadRouteAsync("Kept flight", Recording(12));
        var linked = await CreatePosterAsync("Linked poster", routeId);
        var kept = await CreatePosterAsync("Other poster", otherRouteId);
        var eventId = await CreateEventAsync(2064);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Patch, $"/admin/events/{eventId}", $"{{\"routeId\":{routeId}}}")).StatusCode);

        var preview = await SendAsync(HttpMethod.Get, $"/admin/routes/{routeId}/impact", null);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using (var doc = await ReadJsonAsync(preview))
        {
            var unlinks = doc.RootElement.GetProperty("unlinks").EnumerateArray().ToList();
            var posters = Assert.Single(unlinks, g => g.GetProperty("entity").GetString() == "poster");
            Assert.Equal(1, posters.GetProperty("count").GetInt32());
            Assert.Equal("Linked poster", Assert.Single(posters.GetProperty("names").EnumerateArray()).GetString());
            Assert.Single(unlinks, g => g.GetProperty("entity").GetString() == "event");
            Assert.Empty(doc.RootElement.GetProperty("deletes").EnumerateArray());
        }

        var delete = await SendAsync(HttpMethod.Delete, $"/admin/routes/{routeId}", null);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using (var got = await GetPosterAsync(linked))
            Assert.Equal(JsonValueKind.Null, got.RootElement.GetProperty("routeId").ValueKind);
        using (var got = await GetPosterAsync(kept))
            Assert.Equal(otherRouteId, got.RootElement.GetProperty("routeId").GetInt64());
        Assert.True(await ScalarAsync<bool>($"select route_id is null from event where id = {eventId};"));

        var before = await ScalarAsync<string>(
            $"select before::text from audit_log where entity = 'route' and entity_id = '{routeId}' and action = 'delete';");
        using (var beforeDoc = JsonDocument.Parse(before))
        {
            var unlinks = beforeDoc.RootElement.GetProperty("impact").GetProperty("unlinks").EnumerateArray().ToList();
            var posters = Assert.Single(unlinks, g => g.GetProperty("entity").GetString() == "poster");
            Assert.Equal("Linked poster", Assert.Single(posters.GetProperty("names").EnumerateArray()).GetString());
        }
    }

    [Fact]
    public async Task Route_map_endpoint_answers_for_any_recording_and_404s_an_unknown_id()
    {
        var timedPoints = Recording(100);
        var timed = await UploadRouteAsync("Timed flight", timedPoints);
        var untimedPoints = Recording(60);
        foreach (var p in untimedPoints) p.RecordedAt = null;
        var untimed = await UploadRouteAsync("Untimed flight", untimedPoints);

        foreach (var (routeId, points, isTimed) in new[] { (timed, timedPoints, true), (untimed, untimedPoints, false) })
        {
            var expected = Encoding.UTF8.GetString(CanonicalJson.SerializeToUtf8Bytes(
                RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!));
            foreach (var token in new[] { DevStaticTokens.AdminToken, DevStaticTokens.EditorToken })
            {
                var response = await SendAsync(HttpMethod.Get, $"/admin/routes/{routeId}/route-map", null, token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var doc = await ReadJsonAsync(response);
                var map = doc.RootElement.GetProperty("routeMap");
                Assert.Equal(isTimed, map.GetProperty("timed").GetBoolean());
                Assert.Equal(expected, JsonSerializer.Serialize(map));
            }
        }

        // The recording need not be linked to any event, and the event route-map endpoint stays.
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from event where route_id is not null;"));
        var eventId = await CreateEventAsync(2065);
        var eventMap = await SendAsync(HttpMethod.Get, $"/admin/events/{eventId}/route-map", null, DevStaticTokens.EditorToken);
        Assert.Equal(HttpStatusCode.OK, eventMap.StatusCode);

        var person = await SendAsync(HttpMethod.Get, $"/admin/routes/{timed}/route-map", null, DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.Forbidden, person.StatusCode);

        var missing = await SendAsync(HttpMethod.Get, "/admin/routes/987654321/route-map", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Event_patch_with_posterLayout_is_400_and_the_event_carries_no_posterLayout()
    {
        var id = await CreateEventAsync(2066);

        // posterLayout is an unknown field: the body does not bind and nothing is written.
        var patch = await SendAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"posterLayout\":{\"a\":1}}");
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);

        var mixed = await SendAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"name\":\"Mixed\",\"posterLayout\":null}");
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        Assert.Equal("Event 2066", await ScalarAsync<string>($"select name from event where id = {id};"));

        var ok = await SendAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"name\":\"Renamed\"}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (var patched = await ReadJsonAsync(ok))
            Assert.False(patched.RootElement.TryGetProperty("posterLayout", out _));

        var get = await SendAsync(HttpMethod.Get, $"/admin/events/{id}", null);
        using (var got = await ReadJsonAsync(get))
        {
            Assert.Equal("Renamed", got.RootElement.GetProperty("name").GetString());
            Assert.False(got.RootElement.TryGetProperty("posterLayout", out _));
        }

        using (var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/admin/events", null)))
        {
            foreach (var item in list.RootElement.GetProperty("items").EnumerateArray())
                Assert.False(item.TryGetProperty("posterLayout", out _));
        }

        Assert.False(await ScalarAsync<bool>(
            "select exists (select 1 from information_schema.columns where table_name = 'event' and column_name = 'poster_layout');"));
    }

    // ---------- helpers ----------

    private static async Task AssertValidationFailedOnAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal(ApiErrorCodes.ValidationFailed, doc.RootElement.GetProperty("code").GetString());
        Assert.True(doc.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty(field, out _),
            $"expected a validation failure on {field}");
    }

    private async Task<long> CreatePosterAsync(string name, long? routeId = null)
    {
        var body = routeId is null
            ? $"{{\"name\":\"{name}\"}}"
            : $"{{\"name\":\"{name}\",\"routeId\":{routeId}}}";
        var response = await SendAsync(HttpMethod.Post, "/admin/posters", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    private async Task<JsonDocument> GetPosterAsync(long id)
    {
        var response = await SendAsync(HttpMethod.Get, $"/admin/posters/{id}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<string[]> AuditActionsAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select action from audit_log where entity = 'poster' and entity_id = $1 order by id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var actions = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) actions.Add(reader.GetString(0));
        return actions.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, string? token = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? DevStaticTokens.AdminToken);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host!.Client.SendAsync(req);
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

    // A curving recording, one point every 15 seconds.
    private static List<RoutePoint> Recording(int count)
    {
        var start = DateTimeOffset.Parse("2025-12-22T01:00:00.000Z");
        return Enumerable.Range(0, count)
            .Select(i => new RoutePoint
            {
                Lat = 46.87 + 0.01 * Math.Sin(i * 0.05) + i * 0.0002 + count * 0.001,
                Lng = -114.0 + 0.01 * Math.Cos(i * 0.05),
                RecordedAt = start.AddSeconds(i * 15),
            })
            .ToList();
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

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }
}
