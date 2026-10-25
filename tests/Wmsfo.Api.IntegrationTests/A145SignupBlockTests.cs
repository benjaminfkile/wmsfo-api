using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A145: blocked addresses and domains (contracts 3.1, 4.5 People, sql.md 8.15).
// A block removes every matching person the way a delete does, in one
// transaction with the block row; a directory failure is 502 with nothing
// blocked or removed; first sight of a blocked address is 403 blocked with the
// Cognito user removed and no row written; a blocked subscription address is
// 409 blocked.
public sealed class A145SignupBlockTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly RecordingPersonDirectory _directory = new();
    private A12Host? _host;

    public A145SignupBlockTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await A12MeEndpointsTests.MigrateAndCleanAsync(_fixture.ConnectionString);
        await ExecAsync("delete from signup_block;");
        await ExecAsync("delete from audit_log;");
        _host = await A12Host.StartAsync(_fixture.ConnectionString, directory: _directory);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task An_address_block_removes_the_person_their_subscriber_and_cookies()
    {
        var sub = Guid.NewGuid();
        var id = await SeedPersonWithRowsAsync(sub, "blocked@example.com");

        using var r = await PostBlockAsync("Blocked@Example.com", "spam sign-ups");

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var block = doc.RootElement;
        Assert.Equal("blocked@example.com", block.GetProperty("pattern").GetString());
        Assert.Equal("address", block.GetProperty("kind").GetString());
        Assert.Equal("spam sign-ups", block.GetProperty("note").GetString());
        Assert.Equal("person:" + DevStaticTokens.AdminEmail, block.GetProperty("createdBy").GetString());
        Assert.Equal(0, block.GetProperty("matchingCount").GetInt32());
        Assert.Equal(new[] { sub.ToString() }, _directory.Removals.ToArray());
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from person where id = " + id + ";"));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from subscriber where person_id = " + id + ";"));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from cookie where person_id = " + id + ";"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from signup_block;"));
    }

    [Fact]
    public async Task A_domain_block_removes_two_of_three_people_and_leaves_the_third()
    {
        var a = await InsertPersonAsync(Guid.NewGuid(), "one@somedomain.test");
        var b = await InsertPersonAsync(Guid.NewGuid(), "Two@SomeDomain.test");
        var c = await InsertPersonAsync(Guid.NewGuid(), "three@mail.somedomain.test");

        using var r = await PostBlockAsync("*@somedomain.test", "the whole domain");

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("domain", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(2, _directory.Removals.Count);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from person where id in (" + a + ", " + b + ");"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from person where id = " + c + ";"));
    }

    [Fact]
    public async Task An_address_block_with_no_person_row_removes_the_cognito_user_by_address()
    {
        _directory.Removal = SignInRemoval.Absent;

        using var r = await PostBlockAsync("never.seen@example.com", "signed up, never visited");

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(new[] { "never.seen@example.com" }, _directory.Removals.ToArray());
    }

    [Fact]
    public async Task The_same_pattern_again_is_409_block_exists()
    {
        using (var r = await PostBlockAsync("*@twice.test", "first")) Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        using var again = await PostBlockAsync(" *@TWICE.test ", "second");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("block_exists", await CodeAsync(again));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from signup_block;"));
    }

    [Theory]
    [InlineData("{\"pattern\":\"*.com\",\"note\":\"x\"}", "pattern")]
    [InlineData("{\"pattern\":\"j*@example.com\",\"note\":\"x\"}", "pattern")]
    [InlineData("{\"pattern\":\"jane@example.com\",\"note\":\"  \"}", "note")]
    public async Task A_bad_pattern_or_note_is_400(string body, string field)
    {
        using var r = await SendJsonAsync(HttpMethod.Post, "/admin/people/blocks", body);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", doc.RootElement.GetProperty("code").GetString());
        Assert.True(doc.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty(field, out _));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from signup_block;"));
    }

    [Fact]
    public async Task The_impact_answers_the_matching_people_and_their_counts()
    {
        var id = await SeedPersonWithRowsAsync(Guid.NewGuid(), "impact@impact.test");
        await InsertPersonAsync(Guid.NewGuid(), "other@elsewhere.test");

        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/people/blocks/impact?pattern=" + Uri.EscapeDataString("*@Impact.test"));
        using var r = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("*@impact.test", root.GetProperty("pattern").GetString());
        Assert.Equal("domain", root.GetProperty("kind").GetString());
        var people = root.GetProperty("people").EnumerateArray().ToList();
        Assert.Single(people);
        Assert.Equal(id, people[0].GetProperty("id").GetInt64());
        Assert.Equal("impact@impact.test", people[0].GetProperty("email").GetString());
        Assert.Equal(1, root.GetProperty("subscriptions").GetInt32());
        Assert.Equal(2, root.GetProperty("cookies").GetInt32());
        Assert.Empty(_directory.Removals);
    }

    [Fact]
    public async Task The_impact_refuses_a_bad_pattern()
    {
        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/people/blocks/impact?pattern=" + Uri.EscapeDataString("*"));
        using var r = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("validation_failed", await CodeAsync(r));
    }

    [Fact]
    public async Task A_directory_failure_is_502_and_nothing_is_blocked_or_removed()
    {
        _directory.RemoveFailure = new InvalidOperationException("the pool is unreachable");
        var id = await SeedPersonWithRowsAsync(Guid.NewGuid(), "kept@failing.test");

        using var r = await PostBlockAsync("*@failing.test", "should roll back");

        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Equal("upstream_failed", await CodeAsync(r));
        Assert.Single(_directory.Removals);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from signup_block;"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from person where id = " + id + ";"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from subscriber where person_id = " + id + ";"));
        Assert.Equal(2L, await ScalarAsync<long>("select count(*) from cookie where person_id = " + id + ";"));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from audit_log;"));
    }

    [Fact]
    public async Task The_list_is_newest_first_with_the_matching_count()
    {
        await ExecAsync(@"insert into signup_block (pattern, note, created_by, created_at) values
  ('*@listed.test', 'older', 'person:seed', now() - interval '1 day'),
  ('one@listed.test', 'newer', 'person:seed', now());");
        await InsertPersonAsync(Guid.NewGuid(), "one@listed.test");
        await InsertPersonAsync(Guid.NewGuid(), "two@listed.test");

        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/people/blocks");
        using var r = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("one@listed.test", items[0].GetProperty("pattern").GetString());
        Assert.Equal("address", items[0].GetProperty("kind").GetString());
        Assert.Equal(1, items[0].GetProperty("matchingCount").GetInt32());
        Assert.Equal("*@listed.test", items[1].GetProperty("pattern").GetString());
        Assert.Equal("domain", items[1].GetProperty("kind").GetString());
        Assert.Equal(2, items[1].GetProperty("matchingCount").GetInt32());
        Assert.Equal("older", items[1].GetProperty("note").GetString());
    }

    [Fact]
    public async Task Delete_is_204_and_the_address_may_sign_up_again()
    {
        long id;
        using (var r = await PostBlockAsync("*@wmsfo.test", "temporary"))
        {
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            id = doc.RootElement.GetProperty("id").GetInt64();
        }
        using (var blocked = await GetMeAsync()) Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        using (var del = _host!.AdminRequest(HttpMethod.Delete, "/admin/people/blocks/" + id))
        using (var r = await _host.Client.SendAsync(del))
        {
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }
        using (var again = _host.AdminRequest(HttpMethod.Delete, "/admin/people/blocks/" + id))
        using (var r = await _host.Client.SendAsync(again))
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from signup_block;"));
        using (var me = await GetMeAsync()) Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(1L, await ScalarAsync<long>(
            "select count(*) from person where cognito_sub = '" + DevStaticTokens.PersonSub + "';"));
        Assert.Equal(1L, await ScalarAsync<long>(
            "select count(*) from audit_log where entity = 'signup_block' and action = 'delete' and entity_id = '" + id + "';"));
    }

    [Fact]
    public async Task First_sight_of_a_blocked_address_is_403_removes_the_cognito_user_and_writes_no_row()
    {
        await ExecAsync("insert into signup_block (pattern, note, created_by) values ('"
            + DevStaticTokens.PersonEmail + "', 'blocked', 'person:seed');");

        using var r = await GetMeAsync();

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("blocked", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("this address cannot use the site", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal(new[] { DevStaticTokens.PersonSub }, _directory.Calls.ToArray());
        Assert.Equal(new[] { DevStaticTokens.PersonSub }, _directory.Removals.ToArray());
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from person;"));
    }

    [Fact]
    public async Task First_sight_of_a_blocked_address_is_403_even_when_the_removal_fails()
    {
        _directory.RemoveFailure = new InvalidOperationException("the pool is unreachable");
        await ExecAsync("insert into signup_block (pattern, note, created_by) values ('*@wmsfo.test', 'blocked', 'person:seed');");

        using var r = await GetMeAsync();

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("blocked", await CodeAsync(r));
        Assert.Single(_directory.Removals);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from person;"));
    }

    [Fact]
    public async Task A_subscription_for_a_blocked_address_is_409_blocked_and_writes_nothing()
    {
        using (var b = await PostBlockAsync("*@blocked-alerts.test", "no alerts")) Assert.Equal(HttpStatusCode.Created, b.StatusCode);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/me/subscriptions")
        {
            Content = new StringContent("{\"channel\":\"email\",\"address\":\" Jane@Blocked-Alerts.test \"}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", DevStaticTokens.PersonToken);
        using var r = await _host!.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("blocked", await CodeAsync(r));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from subscriber;"));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from outbox;"));
    }

    [Fact]
    public async Task The_block_and_each_removal_are_audited_by_the_same_actor()
    {
        var a = await InsertPersonAsync(Guid.NewGuid(), "a@audited.test");
        var b = await InsertPersonAsync(Guid.NewGuid(), "b@audited.test");

        long blockId;
        using (var r = await PostBlockAsync("*@audited.test", "audit check"))
        {
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            blockId = doc.RootElement.GetProperty("id").GetInt64();
        }

        var actor = "person:" + DevStaticTokens.AdminEmail;
        using (var after = JsonDocument.Parse(await ScalarAsync<string>(
            "select after::text from audit_log where entity = 'signup_block' and action = 'create' and entity_id = '" + blockId + "';")))
        {
            var root = after.RootElement;
            Assert.Equal("*@audited.test", root.GetProperty("pattern").GetString());
            Assert.Equal("audit check", root.GetProperty("note").GetString());
            var removed = root.GetProperty("removed").EnumerateArray()
                .Select(e => (e.GetProperty("id").GetInt64(), e.GetProperty("email").GetString()))
                .ToArray();
            Assert.Equal(new[] { (a, (string?)"a@audited.test"), (b, (string?)"b@audited.test") }, removed);
        }
        Assert.Equal(actor, await ScalarAsync<string>(
            "select actor from audit_log where entity = 'signup_block' and entity_id = '" + blockId + "';"));
        Assert.Equal(2L, await ScalarAsync<long>(
            "select count(*) from audit_log where entity = 'person' and action = 'delete' and actor = '" + actor
            + "' and entity_id in ('" + a + "', '" + b + "');"));
    }

    private async Task<HttpResponseMessage> PostBlockAsync(string pattern, string note) =>
        await SendJsonAsync(HttpMethod.Post, "/admin/people/blocks",
            JsonSerializer.Serialize(new { pattern, note }));

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> GetMeAsync()
    {
        using var req = _host!.PersonRequest(HttpMethod.Get, "/me");
        return await _host.Client.SendAsync(req);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString();
    }

    private async Task<long> InsertPersonAsync(Guid sub, string email)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var p = new NpgsqlCommand(
            "insert into person (cognito_sub, email) values ($1, $2) returning id;", conn);
        p.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = sub });
        p.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
        return (long)(await p.ExecuteScalarAsync())!;
    }

    // One person with one verified subscription and two cookies on a fresh event.
    private async Task<long> SeedPersonWithRowsAsync(Guid sub, string email)
    {
        var personId = await InsertPersonAsync(sub, email);
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var s = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at)
values ($1, 'email', $2, $3, now());", conn))
        {
            s.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
            s.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
            s.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = Keys.MintUnsubscribeToken().Token });
            await s.ExecuteNonQueryAsync();
        }
        long eventId;
        await using (var e = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values (2033, 'A145 Event', 1, false, 'seed', now()) returning id;", conn))
        {
            eventId = (long)(await e.ExecuteScalarAsync())!;
        }
        long typeId;
        await using (var t = new NpgsqlCommand(@"
insert into cookie_type (name, sort, active, updated_at)
values ($1, 0, true, now()) returning id;", conn))
        {
            t.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "A145Type-" + Guid.NewGuid().ToString("N")[..8] });
            typeId = (long)(await t.ExecuteScalarAsync())!;
        }
        for (var i = 0; i < 2; i++)
        {
            await using var c = new NpgsqlCommand(
                "insert into cookie (event_id, person_id, cookie_type_id) values ($1, $2, $3);", conn);
            c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
            c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
            c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = typeId });
            await c.ExecuteNonQueryAsync();
        }
        return personId;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
