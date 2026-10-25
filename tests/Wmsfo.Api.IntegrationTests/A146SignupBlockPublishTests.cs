using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Chores;

namespace Wmsfo.Api.IntegrationTests;

// A146: the block list reaches the sign-up gate's secret (api.md 4 and 13,
// sql.md 9.9). After a committed POST or DELETE of /admin/people/blocks the
// handler publishes every pattern oldest first; a publisher that throws leaves
// the response unchanged; a write that rolls back publishes nothing. The
// leader's SignupBlockSync reads the same list and logs only when it wrote.
public sealed class A146SignupBlockPublishTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly RecordingPersonDirectory _directory = new();
    private readonly RecordingSignupBlockPublisher _publisher = new();
    private A12Host? _host;

    public A146SignupBlockPublishTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await A12MeEndpointsTests.MigrateAndCleanAsync(_fixture.ConnectionString);
        await ExecAsync("delete from signup_block;");
        _host = await A12Host.StartAsync(_fixture.ConnectionString, directory: _directory,
            signupBlockPublisher: _publisher);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Each_post_publishes_the_full_list_oldest_first_and_a_delete_publishes_again()
    {
        using (var r = await PostBlockAsync("first@example.com", "one")) Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        long secondId;
        using (var r = await PostBlockAsync("*@Second.test", "two"))
        {
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            secondId = doc.RootElement.GetProperty("id").GetInt64();
        }

        Assert.Equal(2, _publisher.Calls.Count);
        Assert.Equal(new[] { "first@example.com" }, _publisher.Calls[0]);
        Assert.Equal(new[] { "first@example.com", "*@second.test" }, _publisher.Calls[1]);

        using (var req = _host!.AdminRequest(HttpMethod.Delete, "/admin/people/blocks/" + secondId))
        using (var r = await _host.Client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }

        Assert.Equal(3, _publisher.Calls.Count);
        Assert.Equal(new[] { "first@example.com" }, _publisher.Calls[2]);
    }

    [Fact]
    public async Task A_publisher_failure_leaves_the_responses_unchanged()
    {
        _publisher.Failure = new InvalidOperationException("the secret is unreachable");

        long id;
        using (var r = await PostBlockAsync("fails@example.com", "publish fails"))
        {
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            id = doc.RootElement.GetProperty("id").GetInt64();
        }
        using (var req = _host!.AdminRequest(HttpMethod.Delete, "/admin/people/blocks/" + id))
        using (var r = await _host.Client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }

        Assert.Equal(2, _publisher.Calls.Count);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from signup_block;"));
    }

    [Fact]
    public async Task A_block_that_rolls_back_publishes_nothing()
    {
        _directory.RemoveFailure = new InvalidOperationException("the pool is unreachable");

        using var r = await PostBlockAsync("rollback@example.com", "cognito fails");

        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Empty(_publisher.Calls);
    }

    [Fact]
    public async Task The_sync_chore_publishes_the_list_and_logs_only_when_it_wrote()
    {
        await ExecAsync("""
            insert into signup_block (pattern, note, created_by, created_at) values
              ('*@later.test', 'b', 'person:seed', now()),
              ('earlier@example.com', 'a', 'person:seed', now() - interval '1 day');
            """);
        var publisher = new RecordingSignupBlockPublisher { Writes = true };
        var logs = new CountingLogger<SignupBlockSync>();
        var sync = new SignupBlockSync(TestConnections.For(_fixture.ConnectionString), publisher, logs);

        Assert.True(await sync.RunOnceAsync(CancellationToken.None));
        Assert.Equal(new[] { "earlier@example.com", "*@later.test" }, Assert.Single(publisher.Calls));
        Assert.Equal(1, logs.Information);

        publisher.Writes = false;
        Assert.False(await sync.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, publisher.Calls.Count);
        Assert.Equal(1, logs.Information);
    }

    private async Task<HttpResponseMessage> PostBlockAsync(string pattern, string note)
    {
        using var req = _host!.AdminRequest(HttpMethod.Post, "/admin/people/blocks");
        req.Content = new StringContent(JsonSerializer.Serialize(new { pattern, note }), Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
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

    private sealed class CountingLogger<T> : ILogger<T>
    {
        public int Information;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information) Interlocked.Increment(ref Information);
        }
    }
}

// Records every list it is handed. `Writes` is what PublishAsync answers;
// `Failure`, when set, is thrown after the call is recorded.
public sealed class RecordingSignupBlockPublisher : ISignupBlockPublisher
{
    private readonly ConcurrentQueue<string[]> _calls = new();

    public bool Writes { get; set; } = true;
    public Exception? Failure { get; set; }
    public IReadOnlyList<string[]> Calls => _calls.ToArray();

    public Task<bool> PublishAsync(IReadOnlyList<string> patterns, CancellationToken ct)
    {
        _calls.Enqueue(patterns.ToArray());
        if (Failure is not null) throw Failure;
        return Task.FromResult(Writes);
    }
}
