using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data.Migrations;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A143: one address is one person (contracts 3.1, sql.md 8.1). The hot path is
// one update by sub with no directory call; first sight asks the people pool
// once, a missing user answers 401 unknown_user with nothing written, and an
// existing one gets a fresh row after any stale row for the address goes with
// its cascade. person_email_lower_key refuses a second row for an address, and
// the migration's guard names the addresses that would break it.
public sealed class A143PersonEmailUniqueTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly RecordingPersonDirectory _directory = new();
    private A12Host? _host;

    public A143PersonEmailUniqueTests(PostgresFixture fixture) { _fixture = fixture; }

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
    public async Task First_sight_of_a_user_the_pool_lacks_is_401_unknown_user_and_writes_nothing()
    {
        _directory.Exists = false;

        using var r = await GetMeAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("unknown_user", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("the token's user no longer exists; sign in again", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal(new[] { DevStaticTokens.PersonSub }, _directory.Calls.ToArray());
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from person;"));
    }

    [Fact]
    public async Task First_sight_inserts_and_the_next_request_takes_the_hot_path()
    {
        using (var r = await GetMeAsync()) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = await PersonIdAsync();
        using (var r = await GetMeAsync()) Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.Single(_directory.Calls);
        Assert.Equal(id, await PersonIdAsync());
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from person;"));
    }

    [Fact]
    public async Task A_stale_row_for_the_address_goes_with_its_subscriber_and_cookies()
    {
        var staleId = await InsertPersonAsync(Guid.NewGuid(), "Person@WMSFO.test");
        await InsertSubscriberAsync(staleId, "stale@example.com");
        var (eventId, typeId) = await SeedEventAndTypeAsync();
        await InsertCookieAsync(eventId, staleId, typeId);
        await InsertCookieAsync(eventId, staleId, typeId);

        using (var r = await GetMeAsync())
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("subscription").ValueKind);
        }

        Assert.Single(_directory.Calls);
        var newId = await PersonIdAsync();
        Assert.NotEqual(staleId, newId);
        Assert.Equal(1L, await ScalarAsync<long>(
            "select count(*) from person where lower(email) = lower('" + DevStaticTokens.PersonEmail + "');"));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from person where id = " + staleId + ";"));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from cookie;"));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from subscriber;"));
    }

    [Fact]
    public async Task The_hot_path_moves_last_seen_at_without_asking_the_pool()
    {
        var id = await InsertPersonAsync(Guid.Parse(DevStaticTokens.PersonSub), DevStaticTokens.PersonEmail);
        await ExecAsync("update person set last_seen_at = now() - interval '1 day' where id = " + id + ";");

        using (var r = await GetMeAsync()) Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.Empty(_directory.Calls);
        Assert.True(await ScalarAsync<bool>(
            "select last_seen_at > now() - interval '1 minute' from person where id = " + id + ";"));
    }

    [Fact]
    public async Task An_email_change_onto_a_stale_rows_address_removes_the_stale_row_and_keeps_the_own_row()
    {
        var ownId = await InsertPersonAsync(Guid.Parse(DevStaticTokens.PersonSub), "before@example.com");
        var staleId = await InsertPersonAsync(Guid.NewGuid(), DevStaticTokens.PersonEmail);

        using (var r = await GetMeAsync()) Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.Single(_directory.Calls);
        Assert.Equal(ownId, await PersonIdAsync());
        Assert.Equal(DevStaticTokens.PersonEmail, await ScalarAsync<string>("select email from person where id = " + ownId + ";"));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from person where id = " + staleId + ";"));
    }

    [Fact]
    public async Task The_unique_index_exists_and_refuses_a_duplicate_insert()
    {
        var definition = await ScalarAsync<string>(
            "select indexdef from pg_indexes where tablename = 'person' and indexname = 'person_email_lower_key';");
        Assert.Contains("UNIQUE INDEX", definition, StringComparison.Ordinal);
        Assert.Contains("lower(email)", definition, StringComparison.Ordinal);

        await InsertPersonAsync(Guid.NewGuid(), "twice@example.com");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertPersonAsync(Guid.NewGuid(), "TWICE@example.com"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal(DbPersonUpsert.EmailLowerKey, ex.ConstraintName);
    }

    [Fact]
    public async Task The_migration_guard_names_the_shared_addresses()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var sql in new[]
        {
            "drop index person_email_lower_key;",
            "insert into person (cognito_sub, email) values (gen_random_uuid(), 'Shared@example.com');",
            "insert into person (cognito_sub, email) values (gen_random_uuid(), 'shared@EXAMPLE.com');",
            "insert into person (cognito_sub, email) values (gen_random_uuid(), 'also@example.com');",
            "insert into person (cognito_sub, email) values (gen_random_uuid(), 'also@example.com');",
            "insert into person (cognito_sub, email) values (gen_random_uuid(), 'alone@example.com');",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var guard = new NpgsqlCommand(A143PersonEmailUnique.GuardSql, conn, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => guard.ExecuteNonQueryAsync());
        Assert.Equal("person rows share an address: also@example.com, shared@example.com", ex.MessageText);
        await tx.RollbackAsync();
    }

    private async Task<HttpResponseMessage> GetMeAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.PersonToken);
        return await _host!.Client.SendAsync(req);
    }

    private Task<long> PersonIdAsync() =>
        ScalarAsync<long>("select id from person where cognito_sub = '" + DevStaticTokens.PersonSub + "';");

    private async Task<long> InsertPersonAsync(Guid sub, string email)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("insert into person (cognito_sub, email) values ($1, $2) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = sub });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task InsertSubscriberAsync(long personId, string address)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at)
values ($1, 'email', $2, $3, now());", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = Keys.MintUnsubscribeToken().Token });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(long EventId, long TypeId)> SeedEventAndTypeAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        long eventId;
        await using (var e = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values (2031, 'A143 Event', 3, true, 'seed', now()) returning id;", conn))
        {
            eventId = (long)(await e.ExecuteScalarAsync())!;
        }
        long typeId;
        await using (var t = new NpgsqlCommand(@"
insert into cookie_type (name, sort, active, updated_at)
values ($1, 0, true, now()) returning id;", conn))
        {
            t.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "A143Type-" + Guid.NewGuid().ToString("N")[..8] });
            typeId = (long)(await t.ExecuteScalarAsync())!;
        }
        return (eventId, typeId);
    }

    private async Task InsertCookieAsync(long eventId, long personId, long typeId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "insert into cookie (event_id, person_id, cookie_type_id) values ($1, $2, $3);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = typeId });
        await cmd.ExecuteNonQueryAsync();
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

// The people pool as the upsert and the person delete see it: answers Exists
// for every sub and records each sub it was asked about; answers Removal for
// every removal, or throws RemoveFailure when set, and records each username.
public sealed class RecordingPersonDirectory : IPersonDirectory
{
    public bool Exists { get; set; } = true;
    public ConcurrentQueue<string> Calls { get; } = new();

    public SignInRemoval Removal { get; set; } = SignInRemoval.Removed;
    public Exception? RemoveFailure { get; set; }
    public ConcurrentQueue<string> Removals { get; } = new();

    public Task<bool> ExistsAsync(string sub, CancellationToken ct)
    {
        Calls.Enqueue(sub);
        return Task.FromResult(Exists);
    }

    public Task<SignInRemoval> RemoveAsync(string username, CancellationToken ct)
    {
        Removals.Enqueue(username);
        if (RemoveFailure is not null) throw RemoveFailure;
        return Task.FromResult(Removal);
    }
}
