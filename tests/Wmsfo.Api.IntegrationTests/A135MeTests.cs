using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Http;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A135: the person's state (contracts 4.4, sql.md 3.12, 8.12a). GET /me answers
// the newest open subscriber row, the seen marker, and the stored preferences;
// PUT /me/preferences replaces the preferences whole; PUT /me/seen only ever
// raises the marker; GET /me/notifications and PUT /me/notifications/seen are
// aliases of the two.
public sealed class A135MeTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string FullPreferences =
        "{\"theme\":\"dark\",\"tracker\":{\"flightHistory\":true,\"timeLabels\":false,\"landmarks\":true," +
        "\"flightDock\":false,\"cookieTally\":true,\"snow\":false,\"mapType\":\"roadmap\"}}";

    private readonly PostgresFixture _fixture;
    private A12Host? _host;

    public A135MeTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await A12MeEndpointsTests.MigrateAndCleanAsync(_fixture.ConnectionString);
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("delete from api_key where name like 'a135-%';", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        _host = await A12Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    // ---------- GET /me ----------

    [Fact]
    public async Task Me_for_a_fresh_person_is_all_null()
    {
        using var r = await SendAsync(HttpMethod.Get, "/me", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await ReadJsonAsync(r);
        Assert.Equal(new[] { "subscription", "seenUpToId", "preferences" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("subscription").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("seenUpToId").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("preferences").ValueKind);
    }

    [Fact]
    public async Task Me_answers_the_subscription_the_marker_and_the_preferences()
    {
        var personId = await EnsurePersonAsync();
        var subscriberId = await InsertSubscriberAsync(personId, "me@example.com");

        using (var r = await PutAsync("/me/seen", "{\"seenUpToId\":31}"))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        using (var r = await PutAsync("/me/preferences", FullPreferences))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        using var me = await SendAsync(HttpMethod.Get, "/me", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var doc = await ReadJsonAsync(me);
        var sub = doc.RootElement.GetProperty("subscription");
        Assert.Equal(subscriberId, sub.GetProperty("id").GetInt64());
        Assert.Equal("me@example.com", sub.GetProperty("address").GetString());
        Assert.Equal(31L, doc.RootElement.GetProperty("seenUpToId").GetInt64());
        AssertJsonEqual(FullPreferences, doc.RootElement.GetProperty("preferences"));
    }

    [Fact]
    public async Task Me_moves_last_seen_at()
    {
        var personId = await EnsurePersonAsync();
        await ExecAsync("update person set last_seen_at = now() - interval '1 day' where id = $1;", personId);

        using var r = await SendAsync(HttpMethod.Get, "/me", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var lastSeen = await ScalarAsync<DateTime>("select last_seen_at from person where id = $1;", personId);
        Assert.True(DateTime.UtcNow - lastSeen.ToUniversalTime() < TimeSpan.FromHours(1));
    }

    // ---------- PUT /me/preferences ----------

    [Theory]
    [InlineData(FullPreferences)]
    [InlineData("{\"theme\":null,\"tracker\":{}}")]
    [InlineData("{\"theme\":\"light\",\"tracker\":{\"snow\":true,\"mapType\":\"terrain\"}}")]
    public async Task Preferences_are_stored_and_read_back_equal(string body)
    {
        await EnsurePersonAsync();
        using (var r = await PutAsync("/me/preferences", body))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        using var me = await SendAsync(HttpMethod.Get, "/me", DevStaticTokens.PersonToken);
        using var doc = await ReadJsonAsync(me);
        AssertJsonEqual(body, doc.RootElement.GetProperty("preferences"));
    }

    [Fact]
    public async Task A_second_put_replaces_the_whole_document()
    {
        var personId = await EnsurePersonAsync();
        using (var r = await PutAsync("/me/preferences", FullPreferences))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        const string second = "{\"theme\":null,\"tracker\":{\"landmarks\":false}}";
        using (var r = await PutAsync("/me/preferences", second))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        using var me = await SendAsync(HttpMethod.Get, "/me", DevStaticTokens.PersonToken);
        using var doc = await ReadJsonAsync(me);
        var prefs = doc.RootElement.GetProperty("preferences");
        AssertJsonEqual(second, prefs);
        Assert.False(prefs.GetProperty("tracker").TryGetProperty("snow", out _));
        Assert.False(prefs.GetProperty("tracker").TryGetProperty("mapType", out _));

        // The stored document is the validated object, nothing more.
        var stored = await ScalarAsync<string>("select preferences::text from person where id = $1;", personId);
        AssertJsonEqual(second, JsonDocument.Parse(stored).RootElement);
    }

    [Fact]
    public async Task Preferences_write_no_outbox_row_and_leave_the_marker()
    {
        var personId = await EnsurePersonAsync();
        await ExecAsync("update person set seen_up_to_id = 9 where id = $1;", personId);
        var before = await ScalarAsync<long>("select count(*) from outbox;", null);
        using (var r = await PutAsync("/me/preferences", FullPreferences))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(before, await ScalarAsync<long>("select count(*) from outbox;", null));
        Assert.Equal(9L, await ScalarAsync<long>("select seen_up_to_id from person where id = $1;", personId));
    }

    [Theory]
    [InlineData("{\"theme\":\"dark\",\"tracker\":{},\"extra\":1}", "extra")]
    [InlineData("{\"theme\":\"dark\",\"tracker\":{\"satellite\":true}}", "tracker.satellite")]
    [InlineData("{\"theme\":\"blue\",\"tracker\":{}}", "theme")]
    [InlineData("{\"tracker\":{}}", "theme")]
    [InlineData("{\"theme\":\"dark\",\"tracker\":{\"snow\":\"yes\"}}", "tracker.snow")]
    [InlineData("{\"theme\":\"dark\",\"tracker\":{\"flightDock\":null}}", "tracker.flightDock")]
    [InlineData("{\"theme\":\"dark\",\"tracker\":{\"mapType\":\"satellite\"}}", "tracker.mapType")]
    [InlineData("{\"theme\":\"dark\",\"tracker\":[]}", "tracker")]
    [InlineData("{\"theme\":\"dark\"}", "tracker")]
    [InlineData("[]", "body")]
    public async Task Preferences_refuse_an_invalid_body_naming_the_field(string body, string field)
    {
        var personId = await EnsurePersonAsync();
        using var r = await PutAsync("/me/preferences", body);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using var doc = await ReadJsonAsync(r);
        Assert.Equal(ApiErrorCodes.ValidationFailed, doc.RootElement.GetProperty("code").GetString());
        Assert.True(doc.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty(field, out _),
            $"expected a field error on {field}: {doc.RootElement}");
        Assert.True(await ScalarAsync<bool>("select preferences is null from person where id = $1;", personId));
    }

    // ---------- PUT /me/seen ----------

    [Fact]
    public async Task Seen_only_ever_raises_the_marker()
    {
        var personId = await EnsurePersonAsync();
        foreach (var (sent, expected) in new[] { (40L, 40L), (55L, 55L), (12L, 55L), (55L, 55L) })
        {
            using (var r = await PutAsync("/me/seen", $"{{\"seenUpToId\":{sent}}}"))
                Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
            Assert.Equal(expected, await ScalarAsync<long>("select seen_up_to_id from person where id = $1;", personId));
        }

        using var me = await SendAsync(HttpMethod.Get, "/me", DevStaticTokens.PersonToken);
        using var doc = await ReadJsonAsync(me);
        Assert.Equal(55L, doc.RootElement.GetProperty("seenUpToId").GetInt64());
    }

    [Theory]
    [InlineData("{\"seenUpToId\":0}")]
    [InlineData("{\"seenUpToId\":-3}")]
    [InlineData("{}")]
    [InlineData("{\"seenUpToId\":null}")]
    public async Task Seen_refuses_a_missing_zero_or_negative_id(string body)
    {
        var personId = await EnsurePersonAsync();
        using var r = await PutAsync("/me/seen", body);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using var doc = await ReadJsonAsync(r);
        Assert.Equal(ApiErrorCodes.ValidationFailed, doc.RootElement.GetProperty("code").GetString());
        Assert.True(doc.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty("seenUpToId", out _));
        Assert.True(await ScalarAsync<bool>("select seen_up_to_id is null from person where id = $1;", personId));
    }

    // ---------- aliases ----------

    [Fact]
    public async Task Notifications_read_answers_the_same_body_as_me()
    {
        var personId = await EnsurePersonAsync();
        await InsertSubscriberAsync(personId, "alias@example.com");
        using (var r = await PutAsync("/me/seen", "{\"seenUpToId\":8}"))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        using (var r = await PutAsync("/me/preferences", FullPreferences))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        using var me = await SendAsync(HttpMethod.Get, "/me", DevStaticTokens.PersonToken);
        using var alias = await SendAsync(HttpMethod.Get, "/me/notifications", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.OK, alias.StatusCode);
        Assert.Equal(await me.Content.ReadAsStringAsync(), await alias.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Notifications_seen_behaves_as_seen()
    {
        var personId = await EnsurePersonAsync();
        using (var r = await PutAsync("/me/notifications/seen", "{\"seenUpToId\":20}"))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(20L, await ScalarAsync<long>("select seen_up_to_id from person where id = $1;", personId));

        using (var r = await PutAsync("/me/notifications/seen", "{\"seenUpToId\":4}"))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(20L, await ScalarAsync<long>("select seen_up_to_id from person where id = $1;", personId));

        using var bad = await PutAsync("/me/notifications/seen", "{\"seenUpToId\":0}");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ---------- authentication and rate ----------

    [Fact]
    public async Task The_three_routes_answer_401_with_an_api_key_only()
    {
        var key = Keys.MintApiKey();
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
insert into api_key (name, key_prefix, key_hash, all_capabilities, created_by)
values ('a135-key', $1, $2, true, 'test');", conn);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key.Prefix });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = key.Hash });
            await cmd.ExecuteNonQueryAsync();
        }

        using (var get = await SendAsync(HttpMethod.Get, "/me", key.Token))
            Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        using (var prefs = await SendAsync(HttpMethod.Put, "/me/preferences", key.Token, FullPreferences))
            Assert.Equal(HttpStatusCode.Unauthorized, prefs.StatusCode);
        using var seen = await SendAsync(HttpMethod.Put, "/me/seen", key.Token, "{\"seenUpToId\":5}");
        Assert.Equal(HttpStatusCode.Unauthorized, seen.StatusCode);
    }

    [Fact]
    public async Task Preferences_answer_429_past_the_burst()
    {
        await using var host = await A12Host.StartAsync(_fixture.ConnectionString, new RateLimitTable());
        for (var i = 0; i < 3; i++)
        {
            using var ok = await SendAsync(host.Client, HttpMethod.Put, "/me/preferences", DevStaticTokens.PersonToken, FullPreferences);
            Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        }
        using var limited = await SendAsync(host.Client, HttpMethod.Put, "/me/preferences", DevStaticTokens.PersonToken, FullPreferences);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        using var doc = await ReadJsonAsync(limited);
        Assert.Equal(ApiErrorCodes.RateLimited, doc.RootElement.GetProperty("code").GetString());

        // The seen write keeps its own bucket.
        using var seen = await SendAsync(host.Client, HttpMethod.Put, "/me/seen", DevStaticTokens.PersonToken, "{\"seenUpToId\":5}");
        Assert.Equal(HttpStatusCode.NoContent, seen.StatusCode);
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

    private async Task<long> InsertSubscriberAsync(long personId, string address)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at)
values ($1, 'email', $2, $3, now())
returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = Keys.MintUnsubscribeToken().Token });
        return (long)(await cmd.ExecuteScalarAsync())!;
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

    // Equal as JSON values: the same keys with the same values at every level,
    // whatever the key order.
    private static void AssertJsonEqual(string expected, JsonElement actual)
    {
        using var e = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(e.RootElement, actual), $"expected {expected}, got {actual}");
    }

    private Task<HttpResponseMessage> PutAsync(string path, string body) =>
        SendAsync(HttpMethod.Put, path, DevStaticTokens.PersonToken, body);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, string? body = null) =>
        SendAsync(_host!.Client, method, path, token, body);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, string? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(req);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());
}
