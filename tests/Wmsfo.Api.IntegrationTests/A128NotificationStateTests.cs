using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A128: the person's notification state (contracts 4.4, sql.md 3.12).
// GET /me/notifications answers the newest open subscriber row and the seen
// marker; PUT /me/notifications/seen only ever raises the marker.
public sealed class A128NotificationStateTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A12Host? _host;

    public A128NotificationStateTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await A12MeEndpointsTests.MigrateAndCleanAsync(_fixture.ConnectionString);
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("delete from api_key where name like 'a128-%';", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        _host = await A12Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    // ---------- GET /me/notifications ----------

    [Fact]
    public async Task Read_with_no_subscriber_row_is_both_null()
    {
        using var r = await SendAsync(HttpMethod.Get, "/me/notifications", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await ReadJsonAsync(r);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("subscription").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("seenUpToId").ValueKind);
    }

    [Fact]
    public async Task Read_moves_last_seen_at()
    {
        var personId = await EnsurePersonAsync();
        await ExecAsync("update person set last_seen_at = now() - interval '1 day' where id = $1;", personId);

        using var r = await SendAsync(HttpMethod.Get, "/me/notifications", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var lastSeen = await ScalarAsync<DateTime>("select last_seen_at from person where id = $1;", personId);
        Assert.True(DateTime.UtcNow - lastSeen.ToUniversalTime() < TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Read_with_an_unsubscribed_row_only_is_null()
    {
        var personId = await EnsurePersonAsync();
        await InsertSubscriberAsync(personId, "gone@example.com",
            verifiedAt: "now() - interval '3 days'", unsubscribedAt: "now() - interval '1 day'", createdAt: "now() - interval '4 days'");

        using var r = await SendAsync(HttpMethod.Get, "/me/notifications", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await ReadJsonAsync(r);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("subscription").ValueKind);
    }

    [Fact]
    public async Task Read_answers_the_newest_open_row_with_its_verified_at()
    {
        var personId = await EnsurePersonAsync();
        var olderActive = await InsertSubscriberAsync(personId, "older@example.com",
            verifiedAt: "now() - interval '9 days'", unsubscribedAt: null, createdAt: "now() - interval '10 days'");
        var newerPending = await InsertSubscriberAsync(personId, "newer@example.com",
            verifiedAt: null, unsubscribedAt: null, createdAt: "now() - interval '1 day'");
        await InsertSubscriberAsync(personId, "newest-gone@example.com",
            verifiedAt: null, unsubscribedAt: "now()", createdAt: "now()");

        using (var r = await SendAsync(HttpMethod.Get, "/me/notifications", DevStaticTokens.PersonToken))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            using var doc = await ReadJsonAsync(r);
            var sub = doc.RootElement.GetProperty("subscription");
            Assert.Equal(newerPending, sub.GetProperty("id").GetInt64());
            Assert.Equal("email", sub.GetProperty("channel").GetString());
            Assert.Equal("newer@example.com", sub.GetProperty("address").GetString());
            Assert.Equal(JsonValueKind.Null, sub.GetProperty("verifiedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, sub.GetProperty("unsubscribedAt").ValueKind);
        }

        // The pending row closed: the older active row is the newest open one,
        // and its verifiedAt is carried.
        await ExecAsync("update subscriber set unsubscribed_at = now() where id = $1;", newerPending);
        using (var r = await SendAsync(HttpMethod.Get, "/me/notifications", DevStaticTokens.PersonToken))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            using var doc = await ReadJsonAsync(r);
            var sub = doc.RootElement.GetProperty("subscription");
            Assert.Equal(olderActive, sub.GetProperty("id").GetInt64());
            Assert.Equal(JsonValueKind.String, sub.GetProperty("verifiedAt").ValueKind);
        }
    }

    [Fact]
    public async Task Read_answers_another_persons_row_never()
    {
        var personId = await EnsurePersonAsync();
        // The editor's own row: the person token must not see it.
        using (var e = await SendAsync(HttpMethod.Get, "/me/notifications", DevStaticTokens.EditorToken))
        {
            Assert.Equal(HttpStatusCode.OK, e.StatusCode);
        }
        var editorId = await ScalarAsync<long>(
            "select id from person where id <> $1 order by id desc limit 1;", personId);
        await InsertSubscriberAsync(editorId, "editor@example.com",
            verifiedAt: "now()", unsubscribedAt: null, createdAt: "now()");

        using var r = await SendAsync(HttpMethod.Get, "/me/notifications", DevStaticTokens.PersonToken);
        using var doc = await ReadJsonAsync(r);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("subscription").ValueKind);
    }

    // ---------- PUT /me/notifications/seen ----------

    [Fact]
    public async Task Write_only_ever_raises_the_marker()
    {
        var personId = await EnsurePersonAsync();

        using (var r = await PutSeenAsync("{\"seenUpToId\":40}"))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(40L, await SeenAsync(personId));
        Assert.Equal(40L, await ReadSeenUpToIdAsync());

        using (var r = await PutSeenAsync("{\"seenUpToId\":55}"))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(55L, await SeenAsync(personId));

        using (var r = await PutSeenAsync("{\"seenUpToId\":12}"))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(55L, await SeenAsync(personId));

        using (var r = await PutSeenAsync("{\"seenUpToId\":55}"))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(55L, await SeenAsync(personId));

        Assert.Equal(55L, await ReadSeenUpToIdAsync());
    }

    [Fact]
    public async Task Write_writes_no_outbox_row()
    {
        await EnsurePersonAsync();
        var before = await ScalarAsync<long>("select count(*) from outbox;", null);
        using (var r = await PutSeenAsync("{\"seenUpToId\":7}"))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(before, await ScalarAsync<long>("select count(*) from outbox;", null));
    }

    [Theory]
    [InlineData("{\"seenUpToId\":0}")]
    [InlineData("{\"seenUpToId\":-3}")]
    [InlineData("{}")]
    [InlineData("{\"seenUpToId\":null}")]
    public async Task Write_refuses_a_missing_zero_or_negative_id(string body)
    {
        var personId = await EnsurePersonAsync();
        using var r = await PutSeenAsync(body);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using var doc = await ReadJsonAsync(r);
        Assert.Equal("validation_failed", doc.RootElement.GetProperty("code").GetString());
        Assert.True(doc.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty("seenUpToId", out _));
        Assert.Null(await SeenAsync(personId));
    }

    [Fact]
    public async Task Both_routes_answer_401_without_a_token()
    {
        using (var get = await _host!.Client.GetAsync("/me/notifications"))
            Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        using var put = await _host!.Client.PutAsync("/me/notifications/seen",
            new StringContent("{\"seenUpToId\":5}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    [Fact]
    public async Task Both_routes_answer_401_with_an_api_key_only()
    {
        var key = Keys.MintApiKey();
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
insert into api_key (name, key_prefix, key_hash, all_capabilities, created_by)
values ('a128-key', $1, $2, true, 'test');", conn);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key.Prefix });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = key.Hash });
            await cmd.ExecuteNonQueryAsync();
        }

        using (var get = await SendAsync(HttpMethod.Get, "/me/notifications", key.Token))
            Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        using var put = await SendAsync(HttpMethod.Put, "/me/notifications/seen", key.Token, "{\"seenUpToId\":5}");
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    // ---------- helpers ----------

    // One authenticated call upserts the person row; answers its id.
    private async Task<long> EnsurePersonAsync()
    {
        using (var r = await SendAsync(HttpMethod.Get, "/me/subscriptions", DevStaticTokens.PersonToken))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select id from person where cognito_sub = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(DevStaticTokens.PersonSub) });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> InsertSubscriberAsync(long personId, string address,
        string? verifiedAt, string? unsubscribedAt, string createdAt)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at, unsubscribed_at, created_at)
values ($1, 'email', $2, $3, {verifiedAt ?? "null"}, {unsubscribedAt ?? "null"}, {createdAt})
returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = Keys.MintUnsubscribeToken().Token });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long?> SeenAsync(long personId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select seen_up_to_id from person where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        var r = await cmd.ExecuteScalarAsync();
        return r is long v ? v : null;
    }

    private async Task<long?> ReadSeenUpToIdAsync()
    {
        using var r = await SendAsync(HttpMethod.Get, "/me/notifications", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await ReadJsonAsync(r);
        var seen = doc.RootElement.GetProperty("seenUpToId");
        return seen.ValueKind == JsonValueKind.Null ? null : seen.GetInt64();
    }

    private async Task ExecAsync(string sql, long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, long? id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (id is not null)
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id.Value });
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<HttpResponseMessage> PutSeenAsync(string body) =>
        SendAsync(HttpMethod.Put, "/me/notifications/seen", DevStaticTokens.PersonToken, body);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, string? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host!.Client.SendAsync(req);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());
}
