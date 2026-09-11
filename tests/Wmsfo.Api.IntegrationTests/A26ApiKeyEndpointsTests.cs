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
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A26 acceptance criteria:
//   - the key matrix over every /admin/* group (all capabilities, one matching
//     capability, the wrong capability, expired, revoked, malformed, a key on
//     the key endpoints)
//   - mint validations, name_taken
//   - a key write records key:<name> in the audit column
public sealed class A26ApiKeyEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A26Host? _host;

    public A26ApiKeyEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAndCleanAsync();
        _host = await A26Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task MigrateAndCleanAsync()
    {
        var opts = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(opts);
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from api_key;",
            "delete from cookie;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from location;",
            "delete from event;",
            "delete from sponsor_year;",
            "delete from sponsor;",
            "delete from cookie_type;",
            "delete from app_setting;",
            "delete from media_asset;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
    }

    // ---------- Mint validations ----------

    [Fact]
    public async Task Mint_returns_201_and_shows_key_once()
    {
        var resp = await SendAdminAsync(HttpMethod.Post, "/admin/api-keys",
            "{\"name\":\"claude-code\",\"allCapabilities\":true,\"capabilities\":[]}");
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        var key = body.RootElement.GetProperty("key").GetString()!;
        Assert.StartsWith("wak_", key);
        Assert.Equal(47, key.Length);
        var prefix = body.RootElement.GetProperty("keyPrefix").GetString()!;
        Assert.Equal(12, prefix.Length);
        Assert.StartsWith(prefix, key);
        Assert.True(body.RootElement.GetProperty("allCapabilities").GetBoolean());
        Assert.Equal(0, body.RootElement.GetProperty("capabilities").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("expiresAt").ValueKind);
        Assert.Equal(DevStaticTokens.AdminEmail, body.RootElement.GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task Mint_all_capabilities_with_non_empty_capabilities_is_400()
    {
        var resp = await SendAdminAsync(HttpMethod.Post, "/admin/api-keys",
            "{\"name\":\"bad\",\"allCapabilities\":true,\"capabilities\":[\"sponsors\"]}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(resp));
    }

    [Fact]
    public async Task Mint_capabilities_empty_when_not_all_is_400()
    {
        var resp = await SendAdminAsync(HttpMethod.Post, "/admin/api-keys",
            "{\"name\":\"bad\",\"allCapabilities\":false,\"capabilities\":[]}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(resp));
    }

    [Fact]
    public async Task Mint_unknown_capability_is_400()
    {
        var resp = await SendAdminAsync(HttpMethod.Post, "/admin/api-keys",
            "{\"name\":\"bad\",\"allCapabilities\":false,\"capabilities\":[\"nope\"]}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(resp));
    }

    [Fact]
    public async Task Mint_duplicate_capability_is_400()
    {
        var resp = await SendAdminAsync(HttpMethod.Post, "/admin/api-keys",
            "{\"name\":\"bad\",\"allCapabilities\":false,\"capabilities\":[\"sponsors\",\"sponsors\"]}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(resp));
    }

    [Fact]
    public async Task Mint_expiry_within_the_hour_is_400()
    {
        var soon = DateTimeOffset.UtcNow.AddMinutes(30).ToString("o");
        var resp = await SendAdminAsync(HttpMethod.Post, "/admin/api-keys",
            "{\"name\":\"bad\",\"allCapabilities\":true,\"capabilities\":[],\"expiresAt\":\"" + soon + "\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(resp));
    }

    [Fact]
    public async Task Mint_expiry_at_least_one_hour_ahead_is_ok()
    {
        var future = DateTimeOffset.UtcNow.AddHours(2).ToString("o");
        var resp = await SendAdminAsync(HttpMethod.Post, "/admin/api-keys",
            "{\"name\":\"okexp\",\"allCapabilities\":true,\"capabilities\":[],\"expiresAt\":\"" + future + "\"}");
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Mint_name_taken_when_unrevoked_key_uses_same_name_is_409()
    {
        var ok = await MintKeyAsync("dup", allCapabilities: true);
        _ = ok;
        var resp = await SendAdminAsync(HttpMethod.Post, "/admin/api-keys",
            "{\"name\":\"dup\",\"allCapabilities\":true,\"capabilities\":[]}");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("name_taken", await ReadCodeAsync(resp));
    }

    [Fact]
    public async Task Mint_name_free_again_after_revoke()
    {
        var mint = await MintKeyAsync("recycled", allCapabilities: true);
        var revoke = await SendAdminAsync(HttpMethod.Post, $"/admin/api-keys/{mint.Id}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var again = await SendAdminAsync(HttpMethod.Post, "/admin/api-keys",
            "{\"name\":\"recycled\",\"allCapabilities\":true,\"capabilities\":[]}");
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    // ---------- List and revoke ----------

    [Fact]
    public async Task List_returns_newest_first_including_revoked()
    {
        var a = await MintKeyAsync("a", allCapabilities: true);
        var b = await MintKeyAsync("b", capabilities: new[] { "sponsors" });
        var revoke = await SendAdminAsync(HttpMethod.Post, $"/admin/api-keys/{a.Id}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var list = await SendAdminAsync(HttpMethod.Get, "/admin/api-keys", content: null);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = await ReadJsonAsync(list);
        var items = body.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(b.Id, items[0].GetProperty("id").GetInt64());
        Assert.Equal(a.Id, items[1].GetProperty("id").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, items[1].GetProperty("revokedAt").ValueKind);
    }

    [Fact]
    public async Task Revoke_is_idempotent()
    {
        var mint = await MintKeyAsync("byebye", allCapabilities: true);
        var first = await SendAdminAsync(HttpMethod.Post, $"/admin/api-keys/{mint.Id}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstAt = (await ReadJsonAsync(first)).RootElement.GetProperty("revokedAt").GetString();
        var second = await SendAdminAsync(HttpMethod.Post, $"/admin/api-keys/{mint.Id}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondAt = (await ReadJsonAsync(second)).RootElement.GetProperty("revokedAt").GetString();
        Assert.Equal(firstAt, secondAt);
    }

    [Fact]
    public async Task Revoke_unknown_id_is_404()
    {
        var resp = await SendAdminAsync(HttpMethod.Post, "/admin/api-keys/999999/revoke", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---------- Deny keys on the key endpoints ----------

    [Fact]
    public async Task Key_on_the_key_endpoints_is_403()
    {
        var (key, _) = await MintKeyReturnKeyAsync("keyholder", allCapabilities: true);

        var list = await SendKeyAsync(HttpMethod.Get, "/admin/api-keys", key, content: null);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);

        var mint = await SendKeyAsync(HttpMethod.Post, "/admin/api-keys", key,
            new StringContent("{\"name\":\"x\",\"allCapabilities\":true,\"capabilities\":[]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, mint.StatusCode);

        var revoke = await SendKeyAsync(HttpMethod.Post, "/admin/api-keys/1/revoke", key, content: null);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
    }

    // ---------- Capability matrix over /admin/* endpoints ----------

    // A key with `sponsors` reaches sponsor endpoints and no other group.
    [Fact]
    public async Task Sponsors_capability_admits_sponsor_endpoints_only()
    {
        var (key, _) = await MintKeyReturnKeyAsync("sp", capabilities: new[] { "sponsors" });

        // Sponsors: OK
        var listSponsors = await SendKeyAsync(HttpMethod.Get, "/admin/sponsors", key, content: null);
        Assert.Equal(HttpStatusCode.OK, listSponsors.StatusCode);

        // Events (needs `events`): 403
        var listEvents = await SendKeyAsync(HttpMethod.Get, "/admin/events", key, content: null);
        Assert.Equal(HttpStatusCode.Forbidden, listEvents.StatusCode);

        // Cookie-types: 403
        var listCookieTypes = await SendKeyAsync(HttpMethod.Get, "/admin/cookie-types", key, content: null);
        Assert.Equal(HttpStatusCode.Forbidden, listCookieTypes.StatusCode);

        // Settings: 403
        var listSettings = await SendKeyAsync(HttpMethod.Get, "/admin/settings", key, content: null);
        Assert.Equal(HttpStatusCode.Forbidden, listSettings.StatusCode);
    }

    // A key with allCapabilities reaches every group except the key endpoints.
    [Fact]
    public async Task All_capabilities_admits_every_group_but_the_key_endpoints()
    {
        var (key, _) = await MintKeyReturnKeyAsync("all", allCapabilities: true);
        foreach (var path in new[]
        {
            "/admin/sponsors",
            "/admin/events",
            "/admin/cookie-types",
            "/admin/settings",
        })
        {
            var resp = await SendKeyAsync(HttpMethod.Get, path, key, content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        var keyList = await SendKeyAsync(HttpMethod.Get, "/admin/api-keys", key, content: null);
        Assert.Equal(HttpStatusCode.Forbidden, keyList.StatusCode);
    }

    // Expired keys answer 401.
    [Fact]
    public async Task Expired_key_answers_401()
    {
        var (key, id) = await MintKeyReturnKeyAsync("exp", allCapabilities: true);
        await BackdateExpiryAsync(id, DateTimeOffset.UtcNow.AddMinutes(-1));

        var resp = await SendKeyAsync(HttpMethod.Get, "/admin/sponsors", key, content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // Revoked keys answer 401.
    [Fact]
    public async Task Revoked_key_answers_401()
    {
        var (key, id) = await MintKeyReturnKeyAsync("rev", allCapabilities: true);
        var revoke = await SendAdminAsync(HttpMethod.Post, $"/admin/api-keys/{id}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var resp = await SendKeyAsync(HttpMethod.Get, "/admin/sponsors", key, content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // Malformed keys (wrong prefix length, non-base64url characters) answer 401.
    [Fact]
    public async Task Malformed_key_answers_401()
    {
        // wak_ prefix but wrong body length.
        var bad = "wak_short";
        var resp = await SendKeyAsync(HttpMethod.Get, "/admin/sponsors", bad, content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // A write with an API key records `key:<name>` in the audit column.
    [Fact]
    public async Task Key_write_records_key_name_in_audit_column()
    {
        var (key, _) = await MintKeyReturnKeyAsync("audit-cat", capabilities: new[] { "events" });

        // Create an event under the key; the event.created_by column carries
        // the audit actor.
        var req = _host!.KeyRequest(HttpMethod.Post, "/admin/events", key);
        req.Content = new StringContent(
            "{\"year\":2027,\"name\":\"tester\",\"statusId\":1,\"fundsPercent\":0}",
            Encoding.UTF8, "application/json");
        var resp = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("key:audit-cat", body.RootElement.GetProperty("createdBy").GetString());

        // Verify by reading directly from the row.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select created_by from event where year = 2027;", conn);
        var stored = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("key:audit-cat", stored);
    }

    // ---------- helpers ----------

    private async Task<(long Id, string Name)> MintKeyAsync(string name, bool allCapabilities = false, string[]? capabilities = null)
    {
        var (_, id) = await MintKeyReturnKeyAsync(name, allCapabilities, capabilities);
        return (id, name);
    }

    private async Task<(string Key, long Id)> MintKeyReturnKeyAsync(string name, bool allCapabilities = false, string[]? capabilities = null)
    {
        var caps = capabilities is null ? "[]" : "[" + string.Join(",", capabilities.Select(c => "\"" + c + "\"")) + "]";
        var body = $"{{\"name\":\"{name}\",\"allCapabilities\":{(allCapabilities ? "true" : "false")},\"capabilities\":{caps}}}";
        var resp = await SendAdminAsync(HttpMethod.Post, "/admin/api-keys", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await ReadJsonAsync(resp);
        return (json.RootElement.GetProperty("key").GetString()!, json.RootElement.GetProperty("id").GetInt64());
    }

    private async Task BackdateExpiryAsync(long id, DateTimeOffset when)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "update api_key set expires_at = $1 where id = $2;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = when });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string? body = null)
    {
        HttpContent? content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json");
        return await SendAdminAsync(method, path, content);
    }

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, HttpContent? content)
    {
        var req = _host!.AdminRequest(method, path);
        if (content is not null) req.Content = content;
        return await _host.Client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> SendKeyAsync(HttpMethod method, string path, string apiKey, HttpContent? content)
    {
        var req = _host!.KeyRequest(method, path, apiKey);
        if (content is not null) req.Content = content;
        return await _host.Client.SendAsync(req);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return JsonDocument.Parse(bytes);
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }
}
