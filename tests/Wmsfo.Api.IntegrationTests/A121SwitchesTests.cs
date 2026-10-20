using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// A121 acceptance: the two switches (contracts 6).
//   - subscriptions_open false: POST /me/subscriptions and resend answer
//     409 subscriptions_closed and write nothing; DELETE, verify, and
//     unsubscribe still work
//   - sign_ups_open with a fake Cognito client: Describe then Update with
//     AllowAdminCreateUserOnly set, then the row, and the rebuilt snapshot
//     carries signUpsOpen false
//   - a Cognito failure is 502 upstream_failed and the row keeps its value
//   - without a Cognito client the write is 503 unavailable and the read works
//   - the boot drift check logs the marker on disagreement and never writes
public sealed class A121SwitchesTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public A121SwitchesTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await A12MeEndpointsTests.MigrateAndCleanAsync(_fixture.ConnectionString);
        await ExecAsync("delete from app_setting where key in ('sign_ups_open', 'subscriptions_open');");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Subscriptions_closed_refuses_new_subscriptions_and_resends_and_nothing_else()
    {
        await using var host = await A12Host.StartAsync(_fixture.ConnectionString);

        // While open: one pending subscription with its verify token, and a
        // second one to delete later.
        using (var created = await SendPersonAsync(host, HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"pending@example.com\"}"))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        long toDelete;
        using (var created = await SendPersonAsync(host, HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"leaving@example.com\"}"))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            toDelete = doc.RootElement.GetProperty("id").GetInt64();
        }
        var (pendingId, verifyToken, unsubscribeToken) = await ReadPendingAsync("pending@example.com");

        await SetSettingAsync("subscriptions_open", "false");
        var subscribersBefore = await CountAsync("select count(*) from subscriber;");
        var outboxBefore = await CountAsync("select count(*) from outbox;");
        var tokenBefore = await ReadVerifyHashAsync(pendingId);

        using (var refused = await SendPersonAsync(host, HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"new@example.com\"}"))
            await AssertClosedAsync(refused);
        // Re-posting the pending address is refused too.
        using (var refused = await SendPersonAsync(host, HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"pending@example.com\"}"))
            await AssertClosedAsync(refused);
        using (var refused = await SendPersonAsync(host, HttpMethod.Post,
            $"/me/subscriptions/{pendingId}/resend-verification", null))
            await AssertClosedAsync(refused);

        Assert.Equal(subscribersBefore, await CountAsync("select count(*) from subscriber;"));
        Assert.Equal(outboxBefore, await CountAsync("select count(*) from outbox;"));
        Assert.Equal(tokenBefore, await ReadVerifyHashAsync(pendingId));

        // The verify link, the unsubscribe link, and DELETE still work.
        using (var verified = await host.Client.PostAsync("/subscriptions/verify",
            new StringContent($"{{\"token\":\"{verifyToken}\"}}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.Equal(1, await CountAsync($"select count(*) from subscriber where id = {pendingId} and verified_at is not null;"));
        using (var unsubscribed = await host.Client.PostAsync("/subscriptions/unsubscribe?token=" + unsubscribeToken, null))
            Assert.Equal(HttpStatusCode.NoContent, unsubscribed.StatusCode);
        using (var deleted = await SendPersonAsync(host, HttpMethod.Delete, $"/me/subscriptions/{toDelete}", null))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await CountAsync("select count(*) from subscriber where unsubscribed_at is null;"));

        // Open again: a new subscription goes through.
        await SetSettingAsync("subscriptions_open", "true");
        using (var created = await SendPersonAsync(host, HttpMethod.Post, "/me/subscriptions",
            "{\"channel\":\"email\",\"address\":\"new@example.com\"}"))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Sign_ups_write_flips_the_pool_then_the_row_and_rebuilds_the_snapshot()
    {
        var cognito = new FakeCognitoClient();
        await using var host = await A11Host.StartAsync(_fixture.ConnectionString, cognito);
        var versionBefore = await CountAsync("select version from snapshot where id = 1;");

        using (var closed = await SendAdminAsync(host, HttpMethod.Put, "/admin/settings/sign_ups_open", "{\"value\":false}"))
        {
            Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            using var doc = JsonDocument.Parse(await closed.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("value").ValueKind);
        }

        // Describe on the issuer's pool, then the full replay with the flag set.
        Assert.Equal(new[] { "describe:us-east-2_pool", "update:us-east-2_pool" }, cognito.Calls);
        Assert.True(cognito.LastUpdate!.AdminCreateUserConfig.AllowAdminCreateUserOnly);
        Assert.Equal(cognito.Pool.Name, cognito.LastUpdate.PoolName);
        Assert.Same(cognito.Pool.LambdaConfig, cognito.LastUpdate.LambdaConfig);
        Assert.Equal("false", await ReadSettingAsync("sign_ups_open"));

        Assert.True(await CountAsync("select version from snapshot where id = 1;") > versionBefore);
        using (var snap = await CurrentSnapshot.ReadAsync(_fixture.ConnectionString, host.Store))
        {
            Assert.False(snap.RootElement.GetProperty("signUpsOpen").GetBoolean());
            Assert.True(snap.RootElement.GetProperty("subscriptionsOpen").GetBoolean());
        }

        // The subscriptions switch is a plain row write and reaches the snapshot too.
        using (var subs = await SendAdminAsync(host, HttpMethod.Put, "/admin/settings/subscriptions_open", "{\"value\":false}"))
            Assert.Equal(HttpStatusCode.OK, subs.StatusCode);
        Assert.Equal(2, cognito.Calls.Count);
        using (var snap = await CurrentSnapshot.ReadAsync(_fixture.ConnectionString, host.Store))
            Assert.False(snap.RootElement.GetProperty("subscriptionsOpen").GetBoolean());

        // GET reports both rows as any setting.
        using (var list = await SendAdminAsync(host, HttpMethod.Get, "/admin/settings", null))
        {
            using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items").EnumerateArray()
                .ToDictionary(i => i.GetProperty("key").GetString()!, i => i.GetProperty("value").ValueKind);
            Assert.Equal(JsonValueKind.False, items["sign_ups_open"]);
            Assert.Equal(JsonValueKind.False, items["subscriptions_open"]);
        }

        // Open again: AllowAdminCreateUserOnly goes back to false.
        using (var opened = await SendAdminAsync(host, HttpMethod.Put, "/admin/settings/sign_ups_open", "{\"value\":true}"))
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.False(cognito.LastUpdate!.AdminCreateUserConfig.AllowAdminCreateUserOnly);
        using (var snap = await CurrentSnapshot.ReadAsync(_fixture.ConnectionString, host.Store))
            Assert.True(snap.RootElement.GetProperty("signUpsOpen").GetBoolean());
    }

    [Fact]
    public async Task A_cognito_failure_is_502_and_the_row_keeps_its_value()
    {
        var cognito = new FakeCognitoClient { FailUpdate = true };
        await using var host = await A11Host.StartAsync(_fixture.ConnectionString, cognito);
        await SetSettingAsync("sign_ups_open", "true");
        var versionBefore = await CountAsync("select version from snapshot where id = 1;");

        using (var failed = await SendAdminAsync(host, HttpMethod.Put, "/admin/settings/sign_ups_open", "{\"value\":false}"))
        {
            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
            Assert.Equal(ApiErrorCodes.UpstreamFailed, await ReadCodeAsync(failed));
        }
        Assert.Equal("true", await ReadSettingAsync("sign_ups_open"));
        Assert.Equal(versionBefore, await CountAsync("select version from snapshot where id = 1;"));

        cognito.FailUpdate = false;
        cognito.FailDescribe = true;
        using (var failed = await SendAdminAsync(host, HttpMethod.Put, "/admin/settings/sign_ups_open", "{\"value\":false}"))
            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Null(cognito.LastUpdate);
        Assert.Equal("true", await ReadSettingAsync("sign_ups_open"));
    }

    [Fact]
    public async Task Without_a_cognito_client_the_write_is_503_and_the_read_works()
    {
        await using var host = await A11Host.StartAsync(_fixture.ConnectionString);

        using (var refused = await SendAdminAsync(host, HttpMethod.Put, "/admin/settings/sign_ups_open", "{\"value\":false}"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal(ApiErrorCodes.Unavailable, await ReadCodeAsync(refused));
        }
        Assert.Null(await ReadSettingAsync("sign_ups_open"));

        using var list = await SendAdminAsync(host, HttpMethod.Get, "/admin/settings", null);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var item = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("key").GetString() == "sign_ups_open");
        Assert.Equal(JsonValueKind.True, item.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("updatedBy").ValueKind);
    }

    [Fact]
    public async Task The_drift_check_logs_the_marker_on_disagreement_and_never_writes()
    {
        var cognito = new FakeCognitoClient();
        cognito.Pool.AdminCreateUserConfig.AllowAdminCreateUserOnly = true;
        var logs = new ListLogger();
        var services = new ServiceCollection().AddSingleton<IAmazonCognitoIdentityProvider>(cognito).BuildServiceProvider();
        var sw = new SignUpsSwitch(services,
            new WmsfoOptions { CognitoIssuer = "https://cognito-idp.us-east-2.amazonaws.com/us-east-2_pool" },
            TestConnections.For(_fixture.ConnectionString), logs);

        // No row means open; the pool is closed.
        await sw.CheckDriftAsync(CancellationToken.None);
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(LogMarkers.SignupsSwitchDrift));
        Assert.Equal(new[] { "describe:us-east-2_pool" }, cognito.Calls);
        Assert.Null(await ReadSettingAsync("sign_ups_open"));

        // Agreement: nothing logged with the marker.
        logs.Entries.Clear();
        await SetSettingAsync("sign_ups_open", "false");
        await sw.CheckDriftAsync(CancellationToken.None);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(LogMarkers.SignupsSwitchDrift));
        Assert.Null(cognito.LastUpdate);
        Assert.Equal("false", await ReadSettingAsync("sign_ups_open"));
    }

    // ---------- helpers ----------

    private static async Task AssertClosedAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.SubscriptionsClosed, doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("new alert sign-ups are closed for now", doc.RootElement.GetProperty("message").GetString());
    }

    private static async Task<HttpResponseMessage> SendPersonAsync(A12Host host, HttpMethod method, string path, string? body)
    {
        using var req = host.PersonRequest(method, path);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await host.Client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> SendAdminAsync(A11Host host, HttpMethod method, string path, string? body)
    {
        using var req = host.AdminRequest(method, path);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await host.Client.SendAsync(req);
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }

    private async Task<(long Id, string VerifyToken, string UnsubscribeToken)> ReadPendingAsync(string address)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
select s.id, o.payload->>'verifyToken', s.unsubscribe_token
from subscriber s
join outbox o on o.topic = 'subscription.verify' and (o.payload->>'subscriberId')::bigint = s.id
where s.address = $1
order by o.id desc limit 1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
    }

    private async Task<byte[]?> ReadVerifyHashAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"select verify_token_hash from subscriber where id = {id};", conn);
        return await cmd.ExecuteScalarAsync() as byte[];
    }

    private async Task SetSettingAsync(string key, string json)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into app_setting (key, value, updated_by) values ($1, $2::jsonb, 'test')
on conflict (key) do update set value = excluded.value;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ReadSettingAsync(string key)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select value::text from app_setting where key = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class ListLogger : ILogger<SignUpsSwitch>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

// A Cognito client that answers DescribeUserPool and UpdateUserPool from
// memory; the described pool sets the fields a real pool carries.
public sealed class FakeCognitoClient : AmazonCognitoIdentityProviderClient
{
    public FakeCognitoClient()
        : base(new AnonymousAWSCredentials(), new AmazonCognitoIdentityProviderConfig { ServiceURL = "http://127.0.0.1:1" })
    {
    }

    public UserPoolType Pool { get; } = new()
    {
        Id = "us-east-2_pool",
        Name = "people",
        Policies = new UserPoolPolicyType { PasswordPolicy = new PasswordPolicyType { MinimumLength = 12, TemporaryPasswordValidityDays = 7 } },
        DeletionProtection = DeletionProtectionType.ACTIVE,
        LambdaConfig = new LambdaConfigType { CustomMessage = "arn:aws:lambda:region:0:function:custom-message" },
        AutoVerifiedAttributes = new List<string> { "email" },
        MfaConfiguration = UserPoolMfaType.OFF,
        UserPoolTier = UserPoolTierType.ESSENTIALS,
        AdminCreateUserConfig = new AdminCreateUserConfigType { AllowAdminCreateUserOnly = false },
    };

    public bool FailDescribe { get; set; }
    public bool FailUpdate { get; set; }
    public List<string> Calls { get; } = new();
    public UpdateUserPoolRequest? LastUpdate { get; private set; }

    public override Task<DescribeUserPoolResponse> DescribeUserPoolAsync(DescribeUserPoolRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add("describe:" + request.UserPoolId);
        if (FailDescribe) throw new AmazonCognitoIdentityProviderException("describe failed");
        return Task.FromResult(new DescribeUserPoolResponse { UserPool = Pool });
    }

    public override Task<UpdateUserPoolResponse> UpdateUserPoolAsync(UpdateUserPoolRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add("update:" + request.UserPoolId);
        if (FailUpdate) throw new AmazonCognitoIdentityProviderException("update failed");
        LastUpdate = request;
        Pool.AdminCreateUserConfig.AllowAdminCreateUserOnly = request.AdminCreateUserConfig.AllowAdminCreateUserOnly;
        return Task.FromResult(new UpdateUserPoolResponse());
    }
}
