using System.Collections.Concurrent;
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

// A10 acceptance criteria: create shows the key once, rotate invalidates the
// old key at the message path and the REST door, revoke is permanent and
// idempotent, activate serializes under beacon_one_active.
public sealed class A10AdminBeaconEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A10Host? _host;

    public A10AdminBeaconEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAsync();
        _host = await A10Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task MigrateAsync()
    {
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(contextOptions);
        await db.Database.MigrateAsync();
        // Fresh state between each test.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from beacon_log;",
            "delete from location;",
            "delete from beacon_enrollment_token;",
            "delete from beacon;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from event;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ---------- Create: shows the key once and returns an enrollment envelope. ----------

    [Fact]
    public async Task Create_returns_beacon_key_and_enrollment_once()
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/beacons",
            new StringContent("{\"name\":\"Helicopter\",\"notes\":\"the sleigh\",\"role\":\"beacon\"}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);

        // The Beacon shape.
        var beacon = body.RootElement.GetProperty("beacon");
        Assert.True(beacon.GetProperty("id").GetInt64() > 0);
        Assert.Equal("Helicopter", beacon.GetProperty("name").GetString());
        Assert.Equal("the sleigh", beacon.GetProperty("notes").GetString());
        Assert.Equal("beacon", beacon.GetProperty("role").GetString());
        Assert.False(beacon.GetProperty("isActive").GetBoolean());
        // keyPrefix is 12 characters of the plaintext key.
        var keyPrefix = beacon.GetProperty("keyPrefix").GetString()!;
        Assert.Equal(12, keyPrefix.Length);

        // The key itself is shown once - it starts with wbk_ and is 47 chars.
        var key = body.RootElement.GetProperty("key").GetString()!;
        Assert.StartsWith("wbk_", key);
        Assert.Equal(47, key.Length);
        Assert.StartsWith(keyPrefix, key);

        var enrollment = body.RootElement.GetProperty("enrollment");
        var token = enrollment.GetProperty("token").GetString()!;
        Assert.StartsWith("wet_", token);
        Assert.Equal(47, token.Length);
        var url = enrollment.GetProperty("url").GetString()!;
        Assert.StartsWith("rednose://enroll?api=", url);
        Assert.Contains(token, url);
        var qr = enrollment.GetProperty("qrPngDataUrl").GetString()!;
        Assert.StartsWith("data:image/png;base64,", qr);
        // A real PNG signature after decoding the base64 - first 8 bytes are the PNG magic.
        var base64 = qr.Substring("data:image/png;base64,".Length);
        var pngBytes = Convert.FromBase64String(base64);
        Assert.True(pngBytes.Length > 16);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            pngBytes.Take(8).ToArray());
    }

    [Fact]
    public async Task Create_second_call_returns_a_different_key()
    {
        var first = await SendAdminAsync(HttpMethod.Post, "/admin/beacons",
            new StringContent("{\"name\":\"one\",\"notes\":\"\",\"role\":\"beacon\"}",
                Encoding.UTF8, "application/json"));
        var second = await SendAdminAsync(HttpMethod.Post, "/admin/beacons",
            new StringContent("{\"name\":\"two\",\"notes\":\"\",\"role\":\"beacon\"}",
                Encoding.UTF8, "application/json"));
        var k1 = (await ReadJsonAsync(first)).RootElement.GetProperty("key").GetString();
        var k2 = (await ReadJsonAsync(second)).RootElement.GetProperty("key").GetString();
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public async Task Create_invalid_role_is_400_validation_failed()
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/beacons",
            new StringContent("{\"name\":\"x\",\"notes\":\"\",\"role\":\"nope\"}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    // ---------- Rotate: invalidates the old key at the message path AND the REST door. ----------

    [Fact]
    public async Task Rotate_invalidates_old_key_at_message_path_and_rest_door()
    {
        // Live event so /locations goes past no_live_event.
        await SeedLiveEventAsync();

        // Create a beacon and remember its key.
        var created = await SendAdminAsync(HttpMethod.Post, "/admin/beacons",
            new StringContent("{\"name\":\"rotor\",\"notes\":\"\",\"role\":\"beacon\"}",
                Encoding.UTF8, "application/json"));
        var beacon = await ReadJsonAsync(created);
        var beaconId = beacon.RootElement.GetProperty("beacon").GetProperty("id").GetInt64();
        var oldKey = beacon.RootElement.GetProperty("key").GetString()!;

        // Sanity: the old key works at the REST door before rotate.
        var meBefore = await SendWithKeyAsync(HttpMethod.Get, "/beacons/me", oldKey);
        Assert.Equal(HttpStatusCode.OK, meBefore.StatusCode);

        // Rotate - response has a new key.
        var rotated = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{beaconId}/rotate", content: null);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var rotatedBody = await ReadJsonAsync(rotated);
        var newKey = rotatedBody.RootElement.GetProperty("key").GetString()!;
        Assert.NotEqual(oldKey, newKey);

        // The REST door: the old key answers 401 (key_hash no longer matches).
        var meAfter = await SendWithKeyAsync(HttpMethod.Get, "/beacons/me", oldKey);
        Assert.Equal(HttpStatusCode.Unauthorized, meAfter.StatusCode);

        // The message path: the identity `<id>:1` no longer matches key_version
        // (rotate bumped it to 2), so the callback answers 403 forbidden.
        var messageBody = "{\"channel\":\"wmsfo-api-test:ingest\",\"event\":\"location\","
                       + "\"data\":{\"lat\":46,\"lng\":-114,\"recordedAt\":\"2026-12-22T01:31:07Z\"},"
                       + $"\"connectionId\":\"c1\",\"identity\":\"{beaconId}:1\"}}";
        var messageResp = await _host!.Client.PostAsync("/realtime/message",
            new StringContent(messageBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, messageResp.StatusCode);

        // The new key still works at the REST door.
        var meNew = await SendWithKeyAsync(HttpMethod.Get, "/beacons/me", newKey);
        Assert.Equal(HttpStatusCode.OK, meNew.StatusCode);

        // And the new identity `<id>:2` succeeds at the message path.
        var messageBody2 = "{\"channel\":\"wmsfo-api-test:ingest\",\"event\":\"location\","
                        + "\"data\":{\"lat\":46,\"lng\":-114,\"recordedAt\":\"2026-12-22T01:31:07Z\"},"
                        + $"\"connectionId\":\"c1\",\"identity\":\"{beaconId}:2\"}}";
        var messageResp2 = await _host.Client.PostAsync("/realtime/message",
            new StringContent(messageBody2, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, messageResp2.StatusCode);
    }

    [Fact]
    public async Task Rotate_on_revoked_beacon_is_409_beacon_revoked()
    {
        var (beaconId, _) = await CreateBeaconAsync("revoked");
        var revoke = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{beaconId}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var rotate = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{beaconId}/rotate", content: null);
        Assert.Equal(HttpStatusCode.Conflict, rotate.StatusCode);
        Assert.Equal("beacon_revoked", await ReadCodeAsync(rotate));
    }

    // ---------- Revoke: permanent and idempotent. ----------

    [Fact]
    public async Task Revoke_is_permanent_and_idempotent()
    {
        var (beaconId, key) = await CreateBeaconAsync("bye");
        // The key works before revoke.
        var meBefore = await SendWithKeyAsync(HttpMethod.Get, "/beacons/me", key);
        Assert.Equal(HttpStatusCode.OK, meBefore.StatusCode);

        var first = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{beaconId}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await ReadJsonAsync(first);
        var firstRevokedAt = firstBody.RootElement.GetProperty("revokedAt").GetString();
        Assert.False(string.IsNullOrEmpty(firstRevokedAt));
        Assert.False(firstBody.RootElement.GetProperty("isActive").GetBoolean());

        // The REST door: 401 immediately (contracts 3.4).
        var meAfter = await SendWithKeyAsync(HttpMethod.Get, "/beacons/me", key);
        Assert.Equal(HttpStatusCode.Unauthorized, meAfter.StatusCode);

        // Idempotent: a second revoke also answers 200. revokedAt does not
        // change (coalesce keeps the original timestamp).
        var second = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{beaconId}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await ReadJsonAsync(second);
        Assert.Equal(firstRevokedAt, secondBody.RootElement.GetProperty("revokedAt").GetString());

        // Row is still there (contracts: revocation is permanent, no delete).
        Assert.Equal(1, await CountBeaconsAsync(beaconId));
    }

    [Fact]
    public async Task Revoke_deletes_pending_enrollment_tokens()
    {
        var created = await SendAdminAsync(HttpMethod.Post, "/admin/beacons",
            new StringContent("{\"name\":\"pend\",\"notes\":\"\",\"role\":\"beacon\"}",
                Encoding.UTF8, "application/json"));
        var body = await ReadJsonAsync(created);
        var id = body.RootElement.GetProperty("beacon").GetProperty("id").GetInt64();
        Assert.Equal(1, await CountEnrollmentTokensAsync(id));
        var revoke = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{id}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(0, await CountEnrollmentTokensAsync(id));
    }

    // ---------- Activate: serializes under beacon_one_active. ----------

    [Fact]
    public async Task Activate_serializes_two_concurrent_calls_under_beacon_one_active()
    {
        // Two beacons; both start inactive.
        var (idA, _) = await CreateBeaconAsync("A");
        var (idB, _) = await CreateBeaconAsync("B");

        // Fire both activates concurrently. At most one can hold beacon_one_active.
        // With sql.md 4.3 retry-once, both eventually succeed but they serialize;
        // the final state has exactly one beacon with is_active = true.
        var t1 = SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{idA}/activate", content: null);
        var t2 = SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{idB}/activate", content: null);
        var responses = await Task.WhenAll(t1, t2);
        foreach (var r in responses)
        {
            // Either 200 (one won), or 200 after retry (the other won on retry).
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        var (activeCount, activeIds) = await ReadActiveBeaconsAsync();
        Assert.Equal(1, activeCount);
        // The active id must be one of the two we activated.
        Assert.Contains(activeIds[0], new[] { idA, idB });
    }

    [Fact]
    public async Task Activate_second_beacon_clears_previous_active()
    {
        var (idA, _) = await CreateBeaconAsync("A2");
        var (idB, _) = await CreateBeaconAsync("B2");

        var r1 = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{idA}/activate", content: null);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var b1 = await ReadJsonAsync(r1);
        Assert.True(b1.RootElement.GetProperty("isActive").GetBoolean());

        var r2 = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{idB}/activate", content: null);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var b2 = await ReadJsonAsync(r2);
        Assert.True(b2.RootElement.GetProperty("isActive").GetBoolean());

        // A is now inactive.
        var getA = await SendAdminAsync(HttpMethod.Get, $"/admin/beacons/{idA}", content: null);
        var aBody = await ReadJsonAsync(getA);
        Assert.False(aBody.RootElement.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Activate_on_revoked_beacon_is_409_beacon_revoked()
    {
        var (id, _) = await CreateBeaconAsync("rev-active");
        await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{id}/revoke", content: null);
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{id}/activate", content: null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("beacon_revoked", await ReadCodeAsync(response));
    }

    // ---------- Deactivate is idempotent. ----------

    [Fact]
    public async Task Deactivate_is_idempotent()
    {
        var (id, _) = await CreateBeaconAsync("deact");
        var r1 = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{id}/deactivate", content: null);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var r2 = await SendAdminAsync(HttpMethod.Post, $"/admin/beacons/{id}/deactivate", content: null);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    // ---------- List and get: hubConnected from presence. ----------

    [Fact]
    public async Task List_hubConnected_resolves_from_presence_call()
    {
        var (id, _) = await CreateBeaconAsync("has-hub");
        // Scripted presence for the ingest channel.
        _host!.Gateway.SetPresence($"{_host.Options.ServiceName}:ingest",
            new List<string> { $"{id}:1" });

        var response = await SendAdminAsync(HttpMethod.Get, "/admin/beacons", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var items = body.RootElement.GetProperty("items");
        var found = false;
        foreach (var item in items.EnumerateArray())
        {
            if (item.GetProperty("id").GetInt64() == id)
            {
                Assert.True(item.GetProperty("hubConnected").GetBoolean());
                found = true;
            }
        }
        Assert.True(found);
    }

    [Fact]
    public async Task Get_hubConnected_null_when_presence_call_fails()
    {
        var (id, _) = await CreateBeaconAsync("no-hub");
        // Presence intentionally unset - the client returns null.
        var response = await SendAdminAsync(HttpMethod.Get, $"/admin/beacons/{id}", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("hubConnected").ValueKind);
    }

    [Fact]
    public async Task List_hubConnected_false_when_identity_not_in_presence()
    {
        var (id, _) = await CreateBeaconAsync("absent");
        // Scripted presence: an unrelated identity is joined.
        _host!.Gateway.SetPresence($"{_host.Options.ServiceName}:ingest",
            new List<string> { "9999:9" });

        var response = await SendAdminAsync(HttpMethod.Get, "/admin/beacons", content: null);
        var body = await ReadJsonAsync(response);
        var items = body.RootElement.GetProperty("items");
        foreach (var item in items.EnumerateArray())
        {
            if (item.GetProperty("id").GetInt64() == id)
            {
                Assert.False(item.GetProperty("hubConnected").GetBoolean());
            }
        }
    }

    // ---------- Patch and Logs sanity. ----------

    [Fact]
    public async Task Patch_updates_name_and_notes()
    {
        var (id, _) = await CreateBeaconAsync("original");
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/admin/beacons/{id}")
        {
            Content = new StringContent("{\"name\":\"renamed\",\"notes\":\"n2\"}",
                Encoding.UTF8, "application/json"),
        };
        patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.AdminToken);
        var response = await _host!.Client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("renamed", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("n2", body.RootElement.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Logs_list_and_fetch_body()
    {
        var (id, key) = await CreateBeaconAsync("logger", role: "admin");
        var post = new HttpRequestMessage(HttpMethod.Post, "/beacons/logs")
        {
            Content = new StringContent("hello there", Encoding.UTF8, "text/plain"),
        };
        post.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var write = await _host!.Client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Created, write.StatusCode);

        var list = await SendAdminAsync(HttpMethod.Get, $"/admin/beacons/{id}/logs", content: null);
        var listBody = await ReadJsonAsync(list);
        var items = listBody.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0);
        var logId = items[0].GetProperty("id").GetInt64();

        var get = await SendAdminAsync(HttpMethod.Get, $"/admin/beacons/{id}/logs/{logId}", content: null);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var text = await get.Content.ReadAsStringAsync();
        Assert.Equal("hello there", text);
    }

    // ---------- helpers ----------

    private async Task<(long Id, string Key)> CreateBeaconAsync(string name, string role = "beacon")
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/beacons",
            new StringContent($"{{\"name\":\"{name}\",\"notes\":\"\",\"role\":\"{role}\"}}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        return (body.RootElement.GetProperty("beacon").GetProperty("id").GetInt64(),
                body.RootElement.GetProperty("key").GetString()!);
    }

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, HttpContent? content)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.AdminToken);
        if (content is not null) req.Content = content;
        return await _host!.Client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> SendWithKeyAsync(HttpMethod method, string path, string key)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        return await _host!.Client.SendAsync(req);
    }

    private async Task SeedLiveEventAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var wipe = new NpgsqlCommand("delete from event where status_id = 3;", conn))
        {
            await wipe.ExecuteNonQueryAsync();
        }
        var year = 2000 + Random.Shared.Next(1, 90);
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, created_by, updated_at)
values ($1, $2, 3, 'seed', now());", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountBeaconsAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*)::int from beacon where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        var r = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(r);
    }

    private async Task<int> CountEnrollmentTokensAsync(long beaconId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select count(*)::int from beacon_enrollment_token where beacon_id = $1 and consumed_at is null;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
        var r = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(r);
    }

    private async Task<(int Count, List<long> Ids)> ReadActiveBeaconsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        var ids = new List<long>();
        await using var cmd = new NpgsqlCommand("select id from beacon where is_active order by id;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
        return (ids.Count, ids);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }
}
