using System.Net;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A144: deleting a person removes their sign-in (contracts 4.5 People, sql.md
// 8.15). The delete asks the people pool to remove the row's sub before any
// delete statement; a removal failure is 502 upstream_failed with every row in
// place; a user the pool no longer has still lets the rows go; the audit row's
// `before` carries `signIn`.
public sealed class A144PersonRemovalTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly RecordingPersonDirectory _directory = new();
    private A12Host? _host;

    public A144PersonRemovalTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await A12MeEndpointsTests.MigrateAndCleanAsync(_fixture.ConnectionString);
        _host = await A12Host.StartAsync(_fixture.ConnectionString, directory: _directory);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Delete_removes_the_sign_in_with_the_rows_sub_then_the_row_its_subscriber_and_cookies()
    {
        var sub = Guid.NewGuid();
        var id = await SeedPersonWithRowsAsync(sub, "removed@example.com");

        using var r = await DeleteAsync(id);

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(new[] { sub.ToString() }, _directory.Removals.ToArray());
        await AssertRowsAsync(id, person: 0, subscribers: 0, cookies: 0);
        Assert.Equal("removed", await AuditSignInAsync(id));
    }

    [Fact]
    public async Task A_removal_failure_is_502_and_nothing_is_deleted()
    {
        _directory.RemoveFailure = new InvalidOperationException("the pool is unreachable");
        var id = await SeedPersonWithRowsAsync(Guid.NewGuid(), "kept@example.com");

        using var r = await DeleteAsync(id);

        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("upstream_failed", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("the sign-in could not be removed; nothing was deleted",
            doc.RootElement.GetProperty("message").GetString());
        Assert.Single(_directory.Removals);
        await AssertRowsAsync(id, person: 1, subscribers: 1, cookies: 2);
        Assert.Equal(0L, await ScalarAsync<long>(
            "select count(*) from audit_log where entity = 'person' and entity_id = '" + id + "';"));
    }

    [Fact]
    public async Task A_user_the_pool_no_longer_has_still_deletes_the_rows()
    {
        _directory.Removal = SignInRemoval.Absent;
        var id = await SeedPersonWithRowsAsync(Guid.NewGuid(), "absent@example.com");

        using var r = await DeleteAsync(id);

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Single(_directory.Removals);
        await AssertRowsAsync(id, person: 0, subscribers: 0, cookies: 0);
        Assert.Equal("absent", await AuditSignInAsync(id));
    }

    [Fact]
    public async Task A_missing_person_is_404_and_the_pool_is_not_asked()
    {
        using var r = await DeleteAsync(987654321);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty(_directory.Removals);
    }

    [Fact]
    public async Task The_audit_row_carries_the_person_the_impact_and_sign_in()
    {
        var id = await SeedPersonWithRowsAsync(Guid.NewGuid(), "audited@example.com");

        using (var r = await DeleteAsync(id)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        using var before = JsonDocument.Parse(await ScalarAsync<string>(
            "select before::text from audit_log where entity = 'person' and entity_id = '" + id + "';"));
        var root = before.RootElement;
        Assert.Equal(id, root.GetProperty("id").GetInt64());
        Assert.Equal("audited@example.com", root.GetProperty("email").GetString());
        var deletes = root.GetProperty("impact").GetProperty("deletes").EnumerateArray()
            .ToDictionary(e => e.GetProperty("entity").GetString()!, e => e.GetProperty("count").GetInt32());
        Assert.Equal(1, deletes["subscriber"]);
        Assert.Equal(2, deletes["cookie"]);
        Assert.Equal("removed", root.GetProperty("signIn").GetString());
    }

    private async Task<HttpResponseMessage> DeleteAsync(long id)
    {
        using var req = _host!.AdminRequest(HttpMethod.Delete, "/admin/people/" + id);
        return await _host.Client.SendAsync(req);
    }

    private async Task<string> AuditSignInAsync(long id) =>
        await ScalarAsync<string>(
            "select before->>'signIn' from audit_log where entity = 'person' and entity_id = '" + id + "';");

    private async Task AssertRowsAsync(long id, long person, long subscribers, long cookies)
    {
        Assert.Equal(person, await ScalarAsync<long>("select count(*) from person where id = " + id + ";"));
        Assert.Equal(subscribers, await ScalarAsync<long>("select count(*) from subscriber where person_id = " + id + ";"));
        Assert.Equal(cookies, await ScalarAsync<long>("select count(*) from cookie where person_id = " + id + ";"));
    }

    // One person with one verified subscription and two cookies on a fresh event.
    private async Task<long> SeedPersonWithRowsAsync(Guid sub, string email)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        long personId;
        await using (var p = new NpgsqlCommand(
            "insert into person (cognito_sub, email) values ($1, $2) returning id;", conn))
        {
            p.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = sub });
            p.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
            personId = (long)(await p.ExecuteScalarAsync())!;
        }
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
values (2032, 'A144 Event', 1, false, 'seed', now()) returning id;", conn))
        {
            eventId = (long)(await e.ExecuteScalarAsync())!;
        }
        long typeId;
        await using (var t = new NpgsqlCommand(@"
insert into cookie_type (name, sort, active, updated_at)
values ($1, 0, true, now()) returning id;", conn))
        {
            t.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "A144Type-" + Guid.NewGuid().ToString("N")[..8] });
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

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
