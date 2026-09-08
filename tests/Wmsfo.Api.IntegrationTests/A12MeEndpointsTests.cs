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

namespace Wmsfo.Api.IntegrationTests;

// Contracts 4.4 registered-person endpoints, sql.md 8.9 and 8.12 for the DB
// transactions.
public sealed class A12MeEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A12Host? _host;

    public A12MeEndpointsTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await MigrateAndCleanAsync(_fixture.ConnectionString);
        _host = await A12Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    internal static async Task MigrateAndCleanAsync(string connectionString)
    {
        var opts = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(opts))
        {
            await db.Database.MigrateAsync();
        }
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from alert_delivery;",
            "delete from outbox;",
            "delete from cookie;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from location;",
            "delete from event;",
            "delete from subscriber;",
            "delete from contact_message;",
            "delete from person;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
    }

    // GET /me

    [Fact]
    public async Task Get_me_returns_person_and_isAdmin_false_for_person()
    {
        using var req = _host!.PersonRequest(HttpMethod.Get, "/me");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(DevStaticTokens.PersonEmail,
            body.RootElement.GetProperty("person").GetProperty("email").GetString());
        Assert.False(body.RootElement.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task Get_me_returns_isAdmin_true_for_admin()
    {
        using var req = _host!.AdminRequest(HttpMethod.Get, "/me");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.RootElement.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task Get_me_without_token_is_401()
    {
        var response = await _host!.Client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // POST /me/subscriptions

    [Fact]
    public async Task Subscribe_new_address_mints_verify_token_and_outbox()
    {
        var response = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"Fresh@Example.COM\"}", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal("fresh@example.com", dto.RootElement.GetProperty("address").GetString());
        Assert.Equal(JsonValueKind.Null, dto.RootElement.GetProperty("verifiedAt").ValueKind);
        // Outbox row inserted with the plaintext token.
        var outboxCount = await CountOutboxAsync("subscription.verify");
        Assert.Equal(1, outboxCount);
    }

    [Fact]
    public async Task Subscribe_repost_pending_returns_200_no_new_outbox()
    {
        // Initial subscribe.
        var first = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"repeat@example.com\"}", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var beforeOutbox = await CountOutboxAsync("subscription.verify");
        // Repeat.
        var second = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"repeat@example.com\"}", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var afterOutbox = await CountOutboxAsync("subscription.verify");
        Assert.Equal(beforeOutbox, afterOutbox);
    }

    [Fact]
    public async Task Subscribe_address_owned_by_other_person_is_409_address_taken()
    {
        // Person A registers address.
        await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"shared@example.com\"}", DevStaticTokens.PersonToken);
        // Person B tries.
        var response = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"shared@example.com\"}", DevStaticTokens.EditorToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("address_taken", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Subscribe_verified_not_unsubscribed_is_409_already_subscribed()
    {
        var create = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"al@example.com\"}", DevStaticTokens.PersonToken);
        var dto = await ReadJsonAsync(create);
        var id = dto.RootElement.GetProperty("id").GetInt64();
        await MarkVerifiedAsync(id);

        var response = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"al@example.com\"}", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("already_subscribed", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Subscribe_reactivate_verified_row_keeps_verifiedAt_no_token()
    {
        var create = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"reup@example.com\"}", DevStaticTokens.PersonToken);
        var dto = await ReadJsonAsync(create);
        var id = dto.RootElement.GetProperty("id").GetInt64();
        await MarkVerifiedAsync(id);
        await MarkUnsubscribedAsync(id);
        var outboxBefore = await CountOutboxAsync("subscription.verify");

        var response = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"reup@example.com\"}", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.NotEqual(JsonValueKind.Null, body.RootElement.GetProperty("verifiedAt").ValueKind);
        var outboxAfter = await CountOutboxAsync("subscription.verify");
        Assert.Equal(outboxBefore, outboxAfter);
    }

    [Fact]
    public async Task Subscribe_bad_channel_is_validation_failed()
    {
        var response = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"sms\",\"address\":\"x@example.com\"}", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Subscribe_bad_address_is_validation_failed()
    {
        var response = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"nope\"}", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    // POST /me/subscriptions/{id}/resend-verification

    [Fact]
    public async Task Resend_verification_writes_new_outbox_row()
    {
        var create = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"resend@example.com\"}", DevStaticTokens.PersonToken);
        var dto = await ReadJsonAsync(create);
        var id = dto.RootElement.GetProperty("id").GetInt64();
        var before = await CountOutboxAsync("subscription.verify");

        using var req = _host!.PersonRequest(HttpMethod.Post, $"/me/subscriptions/{id}/resend-verification");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var after = await CountOutboxAsync("subscription.verify");
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task Resend_verification_on_verified_is_409_already_verified()
    {
        var create = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"done@example.com\"}", DevStaticTokens.PersonToken);
        var dto = await ReadJsonAsync(create);
        var id = dto.RootElement.GetProperty("id").GetInt64();
        await MarkVerifiedAsync(id);

        using var req = _host!.PersonRequest(HttpMethod.Post, $"/me/subscriptions/{id}/resend-verification");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("already_verified", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Resend_verification_unknown_id_is_404()
    {
        using var req = _host!.PersonRequest(HttpMethod.Post, "/me/subscriptions/999999/resend-verification");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // DELETE /me/subscriptions/{id}

    [Fact]
    public async Task Delete_subscription_is_idempotent_204()
    {
        var create = await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"gone@example.com\"}", DevStaticTokens.PersonToken);
        var dto = await ReadJsonAsync(create);
        var id = dto.RootElement.GetProperty("id").GetInt64();

        using var first = _host!.PersonRequest(HttpMethod.Delete, $"/me/subscriptions/{id}");
        Assert.Equal(HttpStatusCode.NoContent, (await _host.Client.SendAsync(first)).StatusCode);
        using var second = _host!.PersonRequest(HttpMethod.Delete, $"/me/subscriptions/{id}");
        Assert.Equal(HttpStatusCode.NoContent, (await _host.Client.SendAsync(second)).StatusCode);
    }

    [Fact]
    public async Task Delete_subscription_unknown_is_404()
    {
        using var req = _host!.PersonRequest(HttpMethod.Delete, "/me/subscriptions/999999");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // GET /me/subscriptions

    [Fact]
    public async Task List_subscriptions_returns_current_persons_rows()
    {
        // Person A
        await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"a1@example.com\"}", DevStaticTokens.PersonToken);
        await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"a2@example.com\"}", DevStaticTokens.PersonToken);
        // Person B (editor)
        await SendJsonAsync(HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"b1@example.com\"}", DevStaticTokens.EditorToken);

        using var req = _host!.PersonRequest(HttpMethod.Get, "/me/subscriptions");
        var response = await _host.Client.SendAsync(req);
        var body = await ReadJsonAsync(response);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        var addresses = items.Select(e => e.GetProperty("address").GetString()!).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "a1@example.com", "a2@example.com" }, addresses);
    }

    // GET /me/cookies

    [Fact]
    public async Task Get_my_cookies_returns_nulls_when_no_current_event()
    {
        using var req = _host!.PersonRequest(HttpMethod.Get, "/me/cookies");
        var response = await _host.Client.SendAsync(req);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("eventId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("eventStatusId").ValueKind);
        Assert.Equal(0, body.RootElement.GetProperty("used").GetInt32());
    }

    [Fact]
    public async Task Get_my_cookies_returns_items_including_hidden_and_computes_remaining()
    {
        var personId = await EnsurePersonIdAsync(DevStaticTokens.PersonSub);
        var eventId = await EnsureLiveEventAsync();
        var typeId = await EnsureCookieTypeAsync("Snickerdoodle", active: true);

        // Two visible, one hidden.
        await InsertCookieAsync(eventId, personId, typeId, hidden: false);
        await InsertCookieAsync(eventId, personId, typeId, hidden: false);
        await InsertCookieAsync(eventId, personId, typeId, hidden: true);

        using var req = _host!.PersonRequest(HttpMethod.Get, "/me/cookies");
        var response = await _host.Client.SendAsync(req);
        var body = await ReadJsonAsync(response);
        Assert.Equal(eventId, body.RootElement.GetProperty("eventId").GetInt64());
        Assert.Equal(3, body.RootElement.GetProperty("eventStatusId").GetInt32());
        Assert.Equal(3, body.RootElement.GetProperty("used").GetInt32());
        Assert.Equal(7, body.RootElement.GetProperty("remaining").GetInt32());
        Assert.Equal(3, body.RootElement.GetProperty("items").GetArrayLength());
    }

    // POST /cookies

    [Fact]
    public async Task Post_cookies_no_live_event_is_409_no_live_event()
    {
        var typeId = await EnsureCookieTypeAsync("Vanilla", active: true);
        var response = await SendJsonAsync(HttpMethod.Post, "/cookies",
            $"{{\"cookieTypeId\":{typeId}}}", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no_live_event", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Post_cookies_unknown_type_is_404_not_found()
    {
        await EnsureLiveEventAsync();
        var response = await SendJsonAsync(HttpMethod.Post, "/cookies",
            "{\"cookieTypeId\":9999999}", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_cookies_inactive_type_is_404_not_found()
    {
        await EnsureLiveEventAsync();
        var typeId = await EnsureCookieTypeAsync("Retired", active: false);
        var response = await SendJsonAsync(HttpMethod.Post, "/cookies",
            $"{{\"cookieTypeId\":{typeId}}}", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_cookies_note_too_long_is_400_validation_failed()
    {
        await EnsureLiveEventAsync();
        var typeId = await EnsureCookieTypeAsync("Vanilla", active: true);
        var note = new string('x', 141);
        var body = $"{{\"cookieTypeId\":{typeId},\"note\":\"{note}\"}}";
        var response = await SendJsonAsync(HttpMethod.Post, "/cookies", body, DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Post_cookies_returns_expected_shape_and_stores_row()
    {
        var eventId = await EnsureLiveEventAsync();
        var typeId = await EnsureCookieTypeAsync("Sugar", active: true);
        var response = await SendJsonAsync(HttpMethod.Post, "/cookies",
            $"{{\"cookieTypeId\":{typeId},\"note\":\"yum\"}}", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal(eventId, dto.RootElement.GetProperty("eventId").GetInt64());
        Assert.Equal(typeId, dto.RootElement.GetProperty("cookieTypeId").GetInt64());
        Assert.Equal("yum", dto.RootElement.GetProperty("note").GetString());
        Assert.True(dto.RootElement.GetProperty("remaining").GetInt32() < 10);

        var stored = await CountCookiesAsync(eventId);
        Assert.Equal(1, stored);
    }

    // Contract acceptance: the person row lock in sql.md 8.9 serializes concurrent
    // inserts for the same person so exactly `limit` cookies are accepted.
    [Fact]
    public async Task Post_cookies_concurrent_inserts_by_one_person_never_exceed_limit()
    {
        var eventId = await EnsureLiveEventAsync();
        var typeId = await EnsureCookieTypeAsync("Concurrent", active: true);
        // Ensure the person row exists so the FOR UPDATE lock has something to
        // grab (the first request would create it, but we want the contention
        // pattern to hit the lock, not the initial upsert).
        _ = await EnsurePersonIdAsync(DevStaticTokens.PersonSub);
        await SetSettingAsync("cookie_limit_per_person", 10);

        // Force everyone to see the same real-DB person upsert result.
        const int attempts = 25;
        var results = new ConcurrentBag<HttpStatusCode>();
        var codes = new ConcurrentBag<string?>();
        var tasks = Enumerable.Range(0, attempts).Select(async _ =>
        {
            using var req = _host!.PersonRequest(HttpMethod.Post, "/cookies");
            req.Content = new StringContent($"{{\"cookieTypeId\":{typeId}}}", Encoding.UTF8, "application/json");
            using var resp = await _host.Client.SendAsync(req);
            results.Add(resp.StatusCode);
            if (resp.StatusCode != HttpStatusCode.Created)
            {
                codes.Add(await ReadCodeAsync(resp));
            }
        });
        await Task.WhenAll(tasks);

        var accepted = results.Count(c => c == HttpStatusCode.Created);
        var stored = await CountCookiesAsync(eventId);
        Assert.Equal(10, accepted);
        Assert.Equal(10, stored);
        // Every rejection carried cookie_limit_reached (once the limit is hit).
        Assert.All(codes, c => Assert.Equal("cookie_limit_reached", c));
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string body, string token)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host!.Client.SendAsync(req);
    }

    private async Task<int> CountOutboxAsync(string topic)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from outbox where topic = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = topic });
        return (int)Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<int> CountCookiesAsync(long eventId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from cookie where event_id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        return (int)Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task MarkVerifiedAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "update subscriber set verified_at = now(), updated_at = now() where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task MarkUnsubscribedAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "update subscriber set unsubscribed_at = now(), updated_at = now() where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> EnsurePersonIdAsync(string sub)
    {
        // The upsert filter runs on the first authorized request that has a sub
        // claim; hit /me to force it.
        using var req = _host!.PersonRequest(HttpMethod.Get, "/me");
        await _host.Client.SendAsync(req);
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select id from person where cognito_sub = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(sub) });
        var r = await cmd.ExecuteScalarAsync();
        if (r is null || r is DBNull) throw new Xunit.Sdk.XunitException("person not found after upsert");
        return Convert.ToInt64(r);
    }

    private async Task<long> EnsureLiveEventAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var check = new NpgsqlCommand(
            "select id from event where status_id = 3 limit 1;", conn);
        var existing = await check.ExecuteScalarAsync();
        if (existing is long l) return l;
        await using var ins = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, 'Test Event', 3, true, 'seed', now()) returning id;", conn);
        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = 2026 + (int)(DateTime.UtcNow.Ticks % 30) });
        var newId = await ins.ExecuteScalarAsync();
        return Convert.ToInt64(newId);
    }

    private async Task<long> EnsureCookieTypeAsync(string name, bool active)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var ins = new NpgsqlCommand(@"
insert into cookie_type (name, sort, active, updated_at)
values ($1, 0, $2, now()) returning id;", conn);
        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = active });
        var r = await ins.ExecuteScalarAsync();
        return Convert.ToInt64(r);
    }

    private async Task InsertCookieAsync(long eventId, long personId, long typeId, bool hidden)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        var sql = hidden
            ? "insert into cookie (event_id, person_id, cookie_type_id, hidden_at, hidden_by) values ($1, $2, $3, now(), 'seed');"
            : "insert into cookie (event_id, person_id, cookie_type_id) values ($1, $2, $3);";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = typeId });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SetSettingAsync(string key, int value)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into app_setting (key, value, updated_by, updated_at)
values ($1, $2::jsonb, 'test', now())
on conflict (key) do update set value = excluded.value, updated_by = excluded.updated_by, updated_at = now();", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await cmd.ExecuteNonQueryAsync();
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
