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

// Contracts 4.5 moderation, inbox, subscribers, people.
public sealed class A12AdminModerationAndInboxTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A12Host? _host;

    public A12AdminModerationAndInboxTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await A12MeEndpointsTests.MigrateAndCleanAsync(_fixture.ConnectionString);
        _host = await A12Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    // Moderation: cookie hide/unhide/delete.

    [Fact]
    public async Task Hide_cookie_sets_hiddenAt_and_hiddenBy()
    {
        var (eventId, personId, typeId) = await SeedEventWithPersonAndTypeAsync(status: 3);
        var cookieId = await InsertCookieAsync(eventId, personId, typeId);

        using var req = _host!.AdminRequest(HttpMethod.Post, $"/admin/cookies/{cookieId}/hide");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.String, dto.RootElement.GetProperty("hiddenAt").ValueKind);
        Assert.Equal(DevStaticTokens.AdminEmail, dto.RootElement.GetProperty("hiddenBy").GetString());
    }

    [Fact]
    public async Task Unhide_cookie_clears_hiddenAt()
    {
        var (eventId, personId, typeId) = await SeedEventWithPersonAndTypeAsync(status: 3);
        var cookieId = await InsertCookieAsync(eventId, personId, typeId);
        await MarkCookieHiddenAsync(cookieId);

        using var req = _host!.AdminRequest(HttpMethod.Post, $"/admin/cookies/{cookieId}/unhide");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Null, dto.RootElement.GetProperty("hiddenAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, dto.RootElement.GetProperty("hiddenBy").ValueKind);
    }

    [Fact]
    public async Task Delete_cookie_is_hard_delete()
    {
        var (eventId, personId, typeId) = await SeedEventWithPersonAndTypeAsync(status: 3);
        var cookieId = await InsertCookieAsync(eventId, personId, typeId);

        using var req = _host!.AdminRequest(HttpMethod.Delete, $"/admin/cookies/{cookieId}");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await CountCookiesAsync(cookieId));
    }

    [Fact]
    public async Task Hide_cookie_unknown_id_is_404()
    {
        using var req = _host!.AdminRequest(HttpMethod.Post, "/admin/cookies/9999999/hide");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Moderation while live event: LiveObjectWriter is called for the current event.
    [Fact]
    public async Task Hide_cookie_while_live_writes_live_object()
    {
        var (eventId, personId, typeId) = await SeedEventWithPersonAndTypeAsync(status: 3);
        _ = eventId;
        var cookieId = await InsertCookieAsync(eventId, personId, typeId);
        var beforePuts = _host!.Store.PutCount("live/location.json");

        using var req = _host.AdminRequest(HttpMethod.Post, $"/admin/cookies/{cookieId}/hide");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // WriteFromState runs on a background task; give it a beat.
        for (var i = 0; i < 50; i++)
        {
            if (_host.Store.PutCount("live/location.json") > beforePuts) break;
            await Task.Delay(50);
        }
        Assert.True(_host.Store.PutCount("live/location.json") > beforePuts);
    }

    // Moderation on a non-live event does not write the live object.
    [Fact]
    public async Task Hide_cookie_while_not_live_does_not_write_live_object()
    {
        var (eventId, personId, typeId) = await SeedEventWithPersonAndTypeAsync(status: 4);
        var cookieId = await InsertCookieAsync(eventId, personId, typeId);
        var beforePuts = _host!.Store.PutCount("live/location.json");

        using var req = _host.AdminRequest(HttpMethod.Post, $"/admin/cookies/{cookieId}/hide");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // No writes are triggered by the moderation on a non-live event.
        await Task.Delay(200);
        Assert.Equal(beforePuts, _host.Store.PutCount("live/location.json"));
    }

    // Inbox: contact messages.

    [Fact]
    public async Task List_contact_messages_paginates_newest_first()
    {
        // Seed three.
        await SeedContactAsync("A");
        await SeedContactAsync("B");
        await SeedContactAsync("C");

        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/contact-messages?limit=2");
        var response = await _host.Client.SendAsync(req);
        var body = await ReadJsonAsync(response);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("C", items[0].GetProperty("name").GetString());
        Assert.Equal("B", items[1].GetProperty("name").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.RootElement.GetProperty("nextCursor").ValueKind);

        var cursor = body.RootElement.GetProperty("nextCursor").GetString();
        using var page2 = _host.AdminRequest(HttpMethod.Get, $"/admin/contact-messages?limit=2&cursor={cursor}");
        var page2Body = await ReadJsonAsync(await _host.Client.SendAsync(page2));
        var page2Items = page2Body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(page2Items);
        Assert.Equal("A", page2Items[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Delete_contact_message_is_204_then_404()
    {
        var id = await SeedContactAsync("Bye");
        using var first = _host!.AdminRequest(HttpMethod.Delete, $"/admin/contact-messages/{id}");
        Assert.Equal(HttpStatusCode.NoContent, (await _host.Client.SendAsync(first)).StatusCode);
        using var second = _host!.AdminRequest(HttpMethod.Delete, $"/admin/contact-messages/{id}");
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.SendAsync(second)).StatusCode);
    }

    // Inbox: subscribers list.

    [Fact]
    public async Task List_subscribers_status_filter_slices_by_state()
    {
        await SeedSubscriberAsync("v@example.com", state: "verified");
        await SeedSubscriberAsync("p@example.com", state: "pending");
        await SeedSubscriberAsync("u@example.com", state: "unsubscribed");

        var byStatus = await ListSubscribersAsync("verified");
        Assert.Single(byStatus);
        Assert.Equal("v@example.com", byStatus[0]);

        byStatus = await ListSubscribersAsync("pending");
        Assert.Single(byStatus);
        Assert.Equal("p@example.com", byStatus[0]);

        byStatus = await ListSubscribersAsync("unsubscribed");
        Assert.Single(byStatus);
        Assert.Equal("u@example.com", byStatus[0]);

        // No filter → all three, newest first (id desc → last inserted first).
        var all = await ListSubscribersAsync(null);
        Assert.Equal(3, all.Length);
    }

    [Fact]
    public async Task List_subscribers_bad_status_is_400()
    {
        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/subscribers?status=nope");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Subscribers_summary_counts_by_state()
    {
        await SeedSubscriberAsync("v@example.com", state: "verified");
        await SeedSubscriberAsync("v2@example.com", state: "verified");
        await SeedSubscriberAsync("p@example.com", state: "pending");
        await SeedSubscriberAsync("u@example.com", state: "unsubscribed");

        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/subscribers/summary");
        var response = await _host.Client.SendAsync(req);
        var body = await ReadJsonAsync(response);
        Assert.Equal(2, body.RootElement.GetProperty("verified").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("pending").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("unsubscribed").GetInt32());
    }

    [Fact]
    public async Task Delete_subscriber_204_then_404()
    {
        var id = await SeedSubscriberAsync("del@example.com", state: "pending");
        using var first = _host!.AdminRequest(HttpMethod.Delete, $"/admin/subscribers/{id}");
        Assert.Equal(HttpStatusCode.NoContent, (await _host.Client.SendAsync(first)).StatusCode);
        using var second = _host!.AdminRequest(HttpMethod.Delete, $"/admin/subscribers/{id}");
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.SendAsync(second)).StatusCode);
    }

    // Inbox: people.

    [Fact]
    public async Task List_people_returns_cookieCount()
    {
        var (eventId, personId, typeId) = await SeedEventWithPersonAndTypeAsync(status: 3);
        _ = eventId;
        _ = await InsertCookieAsync(eventId, personId, typeId);
        _ = await InsertCookieAsync(eventId, personId, typeId);

        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/people");
        var response = await _host.Client.SendAsync(req);
        var body = await ReadJsonAsync(response);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(items);
        var target = items.First(e => e.GetProperty("id").GetInt64() == personId);
        Assert.Equal(2, target.GetProperty("cookieCount").GetInt32());
    }

    [Fact]
    public async Task Delete_person_cascades_subscribers_and_cookies()
    {
        var (eventId, personId, typeId) = await SeedEventWithPersonAndTypeAsync(status: 3);
        _ = eventId;
        _ = await InsertCookieAsync(eventId, personId, typeId);
        _ = await SeedSubscriberForPersonAsync(personId, "for-delete@example.com");

        using var req = _host!.AdminRequest(HttpMethod.Delete, $"/admin/people/{personId}");
        Assert.Equal(HttpStatusCode.NoContent, (await _host.Client.SendAsync(req)).StatusCode);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var count1 = new NpgsqlCommand($"select count(*) from cookie where person_id = {personId};", conn);
        Assert.Equal(0L, (long)(await count1.ExecuteScalarAsync() ?? 0L));
        await using var count2 = new NpgsqlCommand($"select count(*) from subscriber where person_id = {personId};", conn);
        Assert.Equal(0L, (long)(await count2.ExecuteScalarAsync() ?? 0L));
    }

    [Fact]
    public async Task Admin_endpoints_reject_person_token_with_403()
    {
        var (eventId, personId, typeId) = await SeedEventWithPersonAndTypeAsync(status: 3);
        _ = eventId;
        var cookieId = await InsertCookieAsync(eventId, personId, typeId);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/admin/cookies/{cookieId}/hide");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.PersonToken);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<string[]> ListSubscribersAsync(string? status)
    {
        var path = status is null ? "/admin/subscribers" : $"/admin/subscribers?status={status}";
        using var req = _host!.AdminRequest(HttpMethod.Get, path);
        var response = await _host.Client.SendAsync(req);
        var body = await ReadJsonAsync(response);
        return body.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("address").GetString()!).ToArray();
    }

    private async Task<(long eventId, long personId, long typeId)> SeedEventWithPersonAndTypeAsync(int status)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        long eventId;
        await using (var e = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, 'Seed Event', $2, true, 'seed', now()) returning id;", conn))
        {
            e.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = 2020 + (int)(DateTime.UtcNow.Ticks % 60) });
            e.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (short)status });
            eventId = (long)(await e.ExecuteScalarAsync() ?? 0L);
        }
        long personId;
        await using (var p = new NpgsqlCommand(@"
insert into person (cognito_sub, email) values ($1, $2) returning id;", conn))
        {
            p.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.NewGuid() });
            p.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "seed-" + Guid.NewGuid().ToString("N") + "@example.com" });
            personId = (long)(await p.ExecuteScalarAsync() ?? 0L);
        }
        long typeId;
        await using (var t = new NpgsqlCommand(@"
insert into cookie_type (name, sort, active, updated_at)
values ($1, 0, true, now()) returning id;", conn))
        {
            t.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "SeedType-" + Guid.NewGuid().ToString("N")[..8] });
            typeId = (long)(await t.ExecuteScalarAsync() ?? 0L);
        }
        return (eventId, personId, typeId);
    }

    private async Task<long> InsertCookieAsync(long eventId, long personId, long typeId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into cookie (event_id, person_id, cookie_type_id) values ($1, $2, $3) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = typeId });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task MarkCookieHiddenAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "update cookie set hidden_at = now(), hidden_by = 'seed' where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountCookiesAsync(long cookieId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from cookie where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cookieId });
        return (int)Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> SeedContactAsync(string name)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into contact_message (name, email, body, client_ip)
values ($1, 'x@example.com', 'body', '127.0.0.1') returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> SeedSubscriberAsync(string address, string state)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var per = new NpgsqlCommand(@"
insert into person (cognito_sub, email) values ($1, $2) returning id;", conn);
        per.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.NewGuid() });
        per.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "p-" + Guid.NewGuid().ToString("N") + "@example.com" });
        var personId = (long)(await per.ExecuteScalarAsync() ?? 0L);
        return await SeedSubscriberForPersonAsync(personId, address, state);
    }

    private async Task<long> SeedSubscriberForPersonAsync(long personId, string address, string state = "verified")
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        DateTimeOffset? verifiedAt = state switch
        {
            "verified" or "unsubscribed" => DateTimeOffset.UtcNow,
            _ => null,
        };
        DateTimeOffset? unsubscribedAt = state == "unsubscribed" ? DateTimeOffset.UtcNow : null;
        await using var sub = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at, unsubscribed_at)
values ($1, 'email', $2, $3, $4, $5) returning id;", conn);
        sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
        sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = Keys.MintUnsubscribeToken().Token });
        sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)verifiedAt ?? DBNull.Value });
        sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)unsubscribedAt ?? DBNull.Value });
        return (long)(await sub.ExecuteScalarAsync() ?? 0L);
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
