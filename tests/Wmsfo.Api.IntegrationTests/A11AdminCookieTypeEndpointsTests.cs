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

namespace Wmsfo.Api.IntegrationTests;

// A11 cookie types acceptance:
//   - list ordered by sort, id
//   - CRUD (POST + PATCH; no delete)
//   - icon validation: library ids checked against the compiled library; media
//     ids resolved to a ready svg media_asset (400 unknown library id, 400 not
//     svg, 404 media not found, 409 media_not_ready)
//   - 409 event_live when any event carries status_id = 3
public sealed class A11AdminCookieTypeEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A11Host? _host;

    public A11AdminCookieTypeEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAndCleanAsync();
        _host = await A11Host.StartAsync(_fixture.ConnectionString);
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
            "delete from cookie_type;",
            "delete from media_asset;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
    }

    // ---------- CRUD ----------

    [Fact]
    public async Task Create_with_library_icon_returns_201_and_bumps_snapshot()
    {
        var vBefore = await ReadSnapshotVersionAsync();
        var body = "{\"name\":\"Chocolate chip\",\"sort\":10,\"active\":true,\"icon\":{\"source\":\"library\",\"id\":\"cookie\"}}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal("Chocolate chip", dto.RootElement.GetProperty("name").GetString());
        Assert.Equal("library", dto.RootElement.GetProperty("icon").GetProperty("source").GetString());
        Assert.Equal("cookie", dto.RootElement.GetProperty("icon").GetProperty("id").GetString());
        var vAfter = await ReadSnapshotVersionAsync();
        Assert.True(vAfter > vBefore);
    }

    [Fact]
    public async Task Create_with_unknown_library_icon_is_400()
    {
        var body = "{\"name\":\"Bad\",\"sort\":10,\"active\":true,\"icon\":{\"source\":\"library\",\"id\":\"not-a-real-icon-id\"}}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Create_with_media_icon_unknown_media_is_404()
    {
        var body = "{\"name\":\"Bad Media\",\"sort\":10,\"active\":true,\"icon\":{\"source\":\"media\",\"id\":\""
            + Guid.NewGuid() + "\"}}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_media_icon_pending_is_409_media_not_ready()
    {
        var mediaId = await InsertMediaAsync(state: "pending", kind: "svg");
        var body = "{\"name\":\"Pending Icon\",\"sort\":10,\"active\":true,\"icon\":{\"source\":\"media\",\"id\":\""
            + mediaId + "\"}}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("media_not_ready", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Create_with_media_icon_non_svg_is_400()
    {
        var mediaId = await InsertMediaAsync(state: "ready", kind: "raster");
        var body = "{\"name\":\"Raster Icon\",\"sort\":10,\"active\":true,\"icon\":{\"source\":\"media\",\"id\":\""
            + mediaId + "\"}}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Create_with_ready_svg_media_icon_succeeds()
    {
        var mediaId = await InsertMediaAsync(state: "ready", kind: "svg");
        var body = "{\"name\":\"SVG Icon\",\"sort\":10,\"active\":true,\"icon\":{\"source\":\"media\",\"id\":\""
            + mediaId + "\"}}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_bad_sort_range_is_400()
    {
        var body = "{\"name\":\"x\",\"sort\":9999,\"active\":true,\"icon\":null}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_orders_by_sort_then_id()
    {
        var body1 = "{\"name\":\"Late\",\"sort\":50,\"active\":true,\"icon\":null}";
        var body2 = "{\"name\":\"Middle\",\"sort\":20,\"active\":true,\"icon\":null}";
        var body3 = "{\"name\":\"First\",\"sort\":10,\"active\":true,\"icon\":null}";
        await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body1);
        await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body2);
        await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body3);
        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/cookie-types");
        var response = await _host.Client.SendAsync(req);
        var doc = await ReadJsonAsync(response);
        var names = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(new[] { "First", "Middle", "Late" }, names);
    }

    [Fact]
    public async Task Patch_unknown_id_is_404()
    {
        var response = await SendAdminAsync(HttpMethod.Patch, "/admin/cookie-types/999999", "{\"name\":\"x\"}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_active_flag_flips_and_persists()
    {
        var id = await CreateAsync("Toggle", 20, true, "cookie");
        var response = await SendAdminAsync(HttpMethod.Patch, $"/admin/cookie-types/{id}", "{\"active\":false}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.False(dto.RootElement.GetProperty("active").GetBoolean());
    }

    // ---------- 409 event_live guard ----------

    [Fact]
    public async Task Create_while_event_is_live_is_409_event_live()
    {
        await CreateLiveEventAsync(2027);
        var body = "{\"name\":\"Not now\",\"sort\":10,\"active\":true,\"icon\":null}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("event_live", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Patch_while_event_is_live_is_409_event_live()
    {
        var id = await CreateAsync("Off Season", 10, true, "cookie");
        await CreateLiveEventAsync(2028);
        var response = await SendAdminAsync(HttpMethod.Patch, $"/admin/cookie-types/{id}", "{\"active\":false}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("event_live", await ReadCodeAsync(response));
    }

    // ---------- Authorization ----------

    [Fact]
    public async Task Editor_token_cannot_write_cookie_types()
    {
        // Editor is not admin - cookie types are Admin-only.
        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/cookie-types");
        req.Content = new StringContent("{\"name\":\"e\",\"sort\":10,\"active\":true,\"icon\":null}",
            Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Person_token_is_403_forbidden_on_list()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/cookie-types");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.PersonToken);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<long> CreateAsync(string name, int sort, bool active, string libraryIconId)
    {
        var body = "{\"name\":\"" + name + "\",\"sort\":" + sort + ",\"active\":" + (active ? "true" : "false")
            + ",\"icon\":{\"source\":\"library\",\"id\":\"" + libraryIconId + "\"}}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        return dto.RootElement.GetProperty("id").GetInt64();
    }

    private async Task<Guid> InsertMediaAsync(string state, string kind)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into media_asset (id, filename, content_type, kind, state, s3_key, uploaded_by, alt, title, variants)
values ($1, 'file.svg', 'image/svg+xml', $2, $3, 'media/' || $1::text || '/file.svg', 'test', '', '', '{}'::jsonb);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = kind });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = state });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<long> CreateLiveEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var clear = new NpgsqlCommand("update event set is_current = false where is_current;", conn))
            await clear.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, went_live_at, created_by, updated_at)
values ($1, $2, 3, true, now(), 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Live {year}" });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> ReadSnapshotVersionAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select version from snapshot where id = 1;", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }
}
