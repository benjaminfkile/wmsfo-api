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
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// A111: one JSON state document per beacon (contracts 4.2). The API runs as
// the application role and the schema is migrated as the migrate role
// (TestConnections). The first read is empty at version 0; a write names the
// version it read and answers the next one; a stale version is 409 with the
// current version and changes nothing; of two writers on the same version
// exactly one wins; the body rules refuse arrays, null, depth 9, and over
// 16 KB; a revoked key is 401 on both; the read does not stamp last_seen_at;
// GET /beacons/me and the heartbeat carry stateVersion; rotate keeps the
// document; no audit row is written.
public sealed class A111BeaconStateTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A10Host? _host;

    public A111BeaconStateTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.MigrateConnectionString)
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
                "delete from beacon_log;",
                "delete from location;",
                "delete from beacon_enrollment_token;",
                "delete from beacon;",
            })
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        _host = await A10Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task The_first_read_is_an_empty_document_at_version_0()
    {
        var (_, key) = await CreateBeaconAsync("state-first");
        var response = await SendAsync(HttpMethod.Get, "/beacons/me/state", key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("state").ValueKind);
        Assert.Equal(0, body.RootElement.GetProperty("stateVersion").GetInt64());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("updatedAt").ValueKind);
        Assert.NotNull(body.RootElement.GetProperty("serverTime").GetString());
    }

    [Fact]
    public async Task A_write_at_version_0_answers_1_and_the_read_returns_the_document()
    {
        var (id, key) = await CreateBeaconAsync("state-write");
        var auditBefore = await ScalarAsync<long>("select count(*) from audit_log;");
        const string state = "{\"mode\":\"replay\",\"cursor\":{\"index\":1832,\"at\":\"2026-12-22T01:31:07.000Z\"},\"speeds\":[1,2.5,4],\"note\":\"caf\\u00e9\",\"none\":null,\"ok\":true}";

        var write = await PutStateAsync(key, state, expectedVersion: 0);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        using (var body = await ReadJsonAsync(write))
        {
            Assert.Equal(1, body.RootElement.GetProperty("stateVersion").GetInt64());
            Assert.NotNull(body.RootElement.GetProperty("updatedAt").GetString());
            Assert.NotNull(body.RootElement.GetProperty("serverTime").GetString());
        }

        var read = await SendAsync(HttpMethod.Get, "/beacons/me/state", key);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using (var body = await ReadJsonAsync(read))
        {
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(state), JsonNode.Parse(body.RootElement.GetProperty("state").GetRawText())),
                "the read did not return the document written");
            Assert.Equal(1, body.RootElement.GetProperty("stateVersion").GetInt64());
            Assert.NotNull(body.RootElement.GetProperty("updatedAt").GetString());
        }

        // A second write names version 1.
        var again = await PutStateAsync(key, "{\"mode\":\"live\"}", expectedVersion: 1);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using (var body = await ReadJsonAsync(again))
        {
            Assert.Equal(2, body.RootElement.GetProperty("stateVersion").GetInt64());
        }
        Assert.Equal(2L, await ScalarAsync<long>($"select state_version from beacon where id = {id};"));

        // A machine write: no audit row.
        Assert.Equal(auditBefore, await ScalarAsync<long>("select count(*) from audit_log;"));
    }

    [Fact]
    public async Task A_stale_version_is_409_with_the_current_version_and_changes_nothing()
    {
        var (id, key) = await CreateBeaconAsync("state-stale");
        Assert.Equal(HttpStatusCode.OK, (await PutStateAsync(key, "{\"n\":1}", 0)).StatusCode);
        var before = await ScalarAsync<string>($"select state::text || '|' || state_version || '|' || state_updated_at from beacon where id = {id};");

        var stale = await PutStateAsync(key, "{\"n\":2}", 0);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var body = await ReadJsonAsync(stale))
        {
            Assert.Equal(ApiErrorCodes.StateVersionConflict, body.RootElement.GetProperty("code").GetString());
            Assert.Equal(1, body.RootElement.GetProperty("details").GetProperty("stateVersion").GetInt64());
        }

        // A version ahead of the row is stale too.
        var ahead = await PutStateAsync(key, "{\"n\":3}", 7);
        Assert.Equal(HttpStatusCode.Conflict, ahead.StatusCode);

        Assert.Equal(before, await ScalarAsync<string>($"select state::text || '|' || state_version || '|' || state_updated_at from beacon where id = {id};"));
    }

    [Fact]
    public async Task Of_concurrent_writers_on_the_same_version_exactly_one_wins()
    {
        var (id, key) = await CreateBeaconAsync("state-race");
        for (var round = 0; round < 5; round++)
        {
            var expected = round;
            var writers = Enumerable.Range(0, 4)
                .Select(i => PutStateAsync(key, $"{{\"round\":{round},\"writer\":{i}}}", expected))
                .ToArray();
            var answers = await Task.WhenAll(writers);
            Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.OK));
            Assert.Equal(writers.Length - 1, answers.Count(a => a.StatusCode == HttpStatusCode.Conflict));
            foreach (var conflict in answers.Where(a => a.StatusCode == HttpStatusCode.Conflict))
            {
                using var body = await ReadJsonAsync(conflict);
                Assert.Equal(expected + 1, body.RootElement.GetProperty("details").GetProperty("stateVersion").GetInt64());
            }
            Assert.Equal(expected + 1L, await ScalarAsync<long>($"select state_version from beacon where id = {id};"));
        }
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    public async Task A_state_that_is_not_an_object_is_400(string state)
    {
        var (id, key) = await CreateBeaconAsync("state-shape-" + Guid.NewGuid().ToString("N")[..6]);
        var response = await PutRawAsync(key, "{\"state\":" + state + ",\"expectedVersion\":0}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
        Assert.Equal(0L, await ScalarAsync<long>($"select state_version from beacon where id = {id};"));
    }

    [Fact]
    public async Task Depth_9_is_400_and_depth_8_is_stored()
    {
        var (id, key) = await CreateBeaconAsync("state-depth");
        // Counted as the heartbeat's debug: each object is a level and so is the scalar inside the last.
        static string Nested(int levels) =>
            string.Concat(Enumerable.Repeat("{\"a\":", levels - 1)) + "1" + new string('}', levels - 1);

        var deep = await PutStateAsync(key, Nested(9), 0);
        Assert.Equal(HttpStatusCode.BadRequest, deep.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(deep));
        Assert.Equal(0L, await ScalarAsync<long>($"select state_version from beacon where id = {id};"));

        Assert.Equal(HttpStatusCode.OK, (await PutStateAsync(key, Nested(8), 0)).StatusCode);
    }

    [Fact]
    public async Task Missing_or_extra_keys_are_400()
    {
        var (_, key) = await CreateBeaconAsync("state-keys");
        foreach (var body in new[] { "{\"state\":{}}", "{\"expectedVersion\":0}", "{\"state\":{},\"expectedVersion\":0,\"x\":1}", "not json" })
        {
            var response = await PutRawAsync(key, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
        }
    }

    [Fact]
    public async Task A_nul_character_jsonb_cannot_store_is_400_at_state()
    {
        var (id, key) = await CreateBeaconAsync("state-nul");
        var response = await PutRawAsync(key, "{\"state\":{\"a\":\"x\\u0000y\"},\"expectedVersion\":0}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(ApiErrorCodes.ValidationFailed, body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty("state", out _));
        Assert.Equal(0L, await ScalarAsync<long>($"select state_version from beacon where id = {id};"));
    }

    [Fact]
    public async Task A_body_over_16_kb_is_413_and_one_at_the_limit_is_stored()
    {
        var (id, key) = await CreateBeaconAsync("state-big");
        const string head = "{\"state\":{\"pad\":\"";
        const string tail = "\"},\"expectedVersion\":0}";
        var atLimit = head + new string('x', 16 * 1024 - head.Length - tail.Length) + tail;
        Assert.Equal(16 * 1024, Encoding.UTF8.GetByteCount(atLimit));
        var over = head + new string('x', 16 * 1024 - head.Length - tail.Length + 1) + tail;

        var refused = await PutRawAsync(key, over);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Equal(ApiErrorCodes.PayloadTooLarge, await ReadCodeAsync(refused));
        Assert.Equal(0L, await ScalarAsync<long>($"select state_version from beacon where id = {id};"));

        Assert.Equal(HttpStatusCode.OK, (await PutRawAsync(key, atLimit)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_is_401_on_both_and_the_row_keeps_its_document()
    {
        var (id, key) = await CreateBeaconAsync("state-revoked");
        Assert.Equal(HttpStatusCode.OK, (await PutStateAsync(key, "{\"kept\":true}", 0)).StatusCode);

        var revoke = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{id}/revoke");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var read = await SendAsync(HttpMethod.Get, "/beacons/me/state", key);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthenticated, await ReadCodeAsync(read));
        var write = await PutStateAsync(key, "{\"kept\":false}", 1);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthenticated, await ReadCodeAsync(write));

        Assert.Equal("{\"kept\": true}|1", await ScalarAsync<string>($"select state::text || '|' || state_version from beacon where id = {id};"));
    }

    [Fact]
    public async Task An_unknown_key_is_401_on_both()
    {
        var unknown = "wbk_" + new string('a', 43);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/beacons/me/state", unknown)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutStateAsync(unknown, "{}", 0)).StatusCode);
    }

    [Fact]
    public async Task The_read_does_not_stamp_last_seen_at()
    {
        var (id, key) = await CreateBeaconAsync("state-seen");
        await RunSqlAsync($"update beacon set last_seen_at = '2026-01-01T00:00:00Z' where id = {id};");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/beacons/me/state", key)).StatusCode);
        }

        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            await ScalarAsync<DateTime>($"select last_seen_at from beacon where id = {id};"));
    }

    [Fact]
    public async Task Me_and_the_heartbeat_answer_carry_stateVersion()
    {
        var (_, key) = await CreateBeaconAsync("state-version-fields");

        Assert.Equal(0, await ReadMeStateVersionAsync(key));
        Assert.Equal(0, await ReadHeartbeatStateVersionAsync(key));

        Assert.Equal(HttpStatusCode.OK, (await PutStateAsync(key, "{\"a\":1}", 0)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutStateAsync(key, "{\"a\":2}", 1)).StatusCode);

        Assert.Equal(2, await ReadMeStateVersionAsync(key));
        Assert.Equal(2, await ReadHeartbeatStateVersionAsync(key));
    }

    [Fact]
    public async Task Rotate_keeps_the_document_and_its_version()
    {
        var (id, oldKey) = await CreateBeaconAsync("state-rotate");
        const string state = "{\"leader\":\"node-a\",\"cursor\":42}";
        Assert.Equal(HttpStatusCode.OK, (await PutStateAsync(oldKey, state, 0)).StatusCode);

        var rotate = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{id}/rotate");
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        string newKey;
        using (var rotated = await ReadJsonAsync(rotate))
        {
            newKey = rotated.RootElement.GetProperty("key").GetString()!;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/beacons/me/state", oldKey)).StatusCode);

        var read = await SendAsync(HttpMethod.Get, "/beacons/me/state", newKey);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var body = await ReadJsonAsync(read);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(state), JsonNode.Parse(body.RootElement.GetProperty("state").GetRawText())));
        Assert.Equal(1, body.RootElement.GetProperty("stateVersion").GetInt64());
    }

    [Fact]
    public async Task The_app_role_can_read_and_update_the_three_columns()
    {
        var appRole = new NpgsqlConnectionStringBuilder(_fixture.AppConnectionString).Username!;
        foreach (var column in new[] { "state", "state_version", "state_updated_at" })
        {
            foreach (var privilege in new[] { "SELECT", "UPDATE" })
            {
                Assert.True(await ScalarAsync<bool>($"select has_column_privilege('{appRole}', 'beacon', '{column}', '{privilege}');"),
                    $"{appRole} lacks {privilege} on beacon.{column}");
            }
        }
    }

    // ---------- helpers ----------

    private async Task<long> ReadMeStateVersionAsync(string key)
    {
        var me = await SendAsync(HttpMethod.Get, "/beacons/me", key);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var body = await ReadJsonAsync(me);
        return body.RootElement.GetProperty("stateVersion").GetInt64();
    }

    private async Task<long> ReadHeartbeatStateVersionAsync(string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/beacons/heartbeat")
        {
            Content = new StringContent("{\"sentAt\":\"2026-12-22T01:31:07Z\"}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        return body.RootElement.GetProperty("stateVersion").GetInt64();
    }

    private async Task<(long Id, string Key)> CreateBeaconAsync(string name)
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/beacons",
            new StringContent($"{{\"name\":\"{name}\",\"notes\":\"\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        return (body.RootElement.GetProperty("beacon").GetProperty("id").GetInt64(),
                body.RootElement.GetProperty("key").GetString()!);
    }

    private Task<HttpResponseMessage> PutStateAsync(string key, string state, long expectedVersion) =>
        PutRawAsync(key, "{\"state\":" + state + ",\"expectedVersion\":" + expectedVersion + "}");

    private async Task<HttpResponseMessage> PutRawAsync(string key, string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, "/beacons/me/state")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        return await _host!.Client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string key)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        return await _host!.Client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.AdminToken);
        if (content is not null) req.Content = content;
        return await _host!.Client.SendAsync(req);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    private async Task RunSqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage r)
    {
        using var doc = await ReadJsonAsync(r);
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());
}
