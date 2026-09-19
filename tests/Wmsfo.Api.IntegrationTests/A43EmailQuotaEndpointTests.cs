using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Email;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A43: contracts 4.5 Email quota. GET /admin/email/quota answers 200 EmailQuota
// for admin and editor Cognito tokens, 401 without a credential, 403 for a
// person token; API keys cannot call it. The three SES numbers pass through
// the fake reader; queued counts unsent alert_delivery rows; verifiedSubscribers
// counts subscriber rows with the alert-fan-out filter (7.7); remaining is
// max - sent - queued floored at 0 and null with no limit; wouldExceed is
// verifiedSubscribers > remaining.
public sealed class A43EmailQuotaEndpointTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A12Host? _host;

    public A43EmailQuotaEndpointTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await A12MeEndpointsTests.MigrateAndCleanAsync(_fixture.ConnectionString);
        _host = await A12Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Returns_200_with_would_exceed_true_when_headroom_is_below_subscribers()
    {
        _host!.QuotaReader.NextReading = new EmailQuotaReading(true, 50000, 49000, 14);
        await SeedQueuedDeliveriesAsync(2);
        await SeedVerifiedSubscribersAsync(1500);

        using var req = _host.AdminRequest(HttpMethod.Get, "/admin/email/quota");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.False(root.GetProperty("dryRun").GetBoolean());
        Assert.Equal(50000d, root.GetProperty("max24HourSend").GetDouble());
        Assert.Equal(49000d, root.GetProperty("sentLast24Hours").GetDouble());
        Assert.Equal(14d, root.GetProperty("maxSendRate").GetDouble());
        Assert.Equal(2, root.GetProperty("queued").GetInt32());
        Assert.Equal(1500, root.GetProperty("verifiedSubscribers").GetInt32());
        Assert.Equal(998d, root.GetProperty("remaining").GetDouble());
        Assert.True(root.GetProperty("wouldExceed").GetBoolean());
    }

    [Fact]
    public async Task Would_exceed_is_false_when_subscribers_fit_inside_remaining()
    {
        _host!.QuotaReader.NextReading = new EmailQuotaReading(true, 50000, 49000, 14);
        await SeedQueuedDeliveriesAsync(2);
        await SeedVerifiedSubscribersAsync(10);

        using var req = _host.AdminRequest(HttpMethod.Get, "/admin/email/quota");
        var response = await _host.Client.SendAsync(req);
        var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(998d, root.GetProperty("remaining").GetDouble());
        Assert.Equal(10, root.GetProperty("verifiedSubscribers").GetInt32());
        Assert.False(root.GetProperty("wouldExceed").GetBoolean());
    }

    [Fact]
    public async Task Unavailable_reading_gives_null_ses_numbers_and_null_remaining()
    {
        _host!.QuotaReader.NextReading = EmailQuotaReading.Unavailable();
        await SeedQueuedDeliveriesAsync(3);
        await SeedVerifiedSubscribersAsync(4);

        using var req = _host.AdminRequest(HttpMethod.Get, "/admin/email/quota");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("max24HourSend").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sentLast24Hours").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("maxSendRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("remaining").ValueKind);
        Assert.Equal(3, root.GetProperty("queued").GetInt32());
        Assert.Equal(4, root.GetProperty("verifiedSubscribers").GetInt32());
        Assert.False(root.GetProperty("wouldExceed").GetBoolean());
    }

    [Fact]
    public async Task No_limit_gives_null_remaining_and_would_exceed_false()
    {
        _host!.QuotaReader.NextReading = new EmailQuotaReading(true, null, 100, 14);
        await SeedQueuedDeliveriesAsync(1);
        await SeedVerifiedSubscribersAsync(99999);

        using var req = _host.AdminRequest(HttpMethod.Get, "/admin/email/quota");
        var response = await _host.Client.SendAsync(req);
        var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("max24HourSend").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("remaining").ValueKind);
        Assert.False(root.GetProperty("wouldExceed").GetBoolean());
    }

    [Fact]
    public async Task Editor_token_is_admitted()
    {
        _host!.QuotaReader.NextReading = new EmailQuotaReading(true, 200, 10, 1);
        using var req = _host.EditorRequest(HttpMethod.Get, "/admin/email/quota");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Missing_credential_is_401()
    {
        var response = await _host!.Client.GetAsync("/admin/email/quota");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Person_token_is_403()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/email/quota");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.PersonToken);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Helpers.

    private async Task SeedVerifiedSubscribersAsync(int n)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        for (var i = 0; i < n; i++)
        {
            long personId;
            await using (var per = new NpgsqlCommand(
                "insert into person (cognito_sub, email) values ($1, $2) returning id;", conn))
            {
                per.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.NewGuid() });
                per.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "p-" + Guid.NewGuid().ToString("N") + "@example.com" });
                personId = (long)(await per.ExecuteScalarAsync() ?? 0L);
            }
            await using var sub = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at, unsubscribed_at)
values ($1, 'email', $2, $3, now(), null);", conn);
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "v-" + Guid.NewGuid().ToString("N") + "@example.com" });
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = Keys.MintUnsubscribeToken().Token });
            await sub.ExecuteNonQueryAsync();
        }
    }

    private async Task SeedQueuedDeliveriesAsync(int n)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        // A queued alert_delivery row needs its outbox parent and a subscriber
        // reference. Both are seeded here; sent_at stays null so the row counts.
        long outboxId;
        await using (var ob = new NpgsqlCommand(@"
insert into outbox (topic, payload) values ('event.status_changed', '{}'::jsonb) returning id;", conn))
        {
            outboxId = (long)(await ob.ExecuteScalarAsync() ?? 0L);
        }
        long personId;
        await using (var per = new NpgsqlCommand(
            "insert into person (cognito_sub, email) values ($1, $2) returning id;", conn))
        {
            per.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.NewGuid() });
            per.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "q-" + Guid.NewGuid().ToString("N") + "@example.com" });
            personId = (long)(await per.ExecuteScalarAsync() ?? 0L);
        }
        for (var i = 0; i < n; i++)
        {
            // The queued subscriber is intentionally unverified so the
            // verifiedSubscribers count in the tests stays at what
            // SeedVerifiedSubscribersAsync creates.
            long subId;
            await using (var sub = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at, unsubscribed_at)
values ($1, 'email', $2, $3, null, null) returning id;", conn))
            {
                sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "q-sub-" + i + "-" + Guid.NewGuid().ToString("N") + "@example.com" });
                sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = Keys.MintUnsubscribeToken().Token });
                subId = (long)(await sub.ExecuteScalarAsync() ?? 0L);
            }
            await using var del = new NpgsqlCommand(@"
insert into alert_delivery (outbox_id, subscriber_id) values ($1, $2);", conn);
            del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = outboxId });
            del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = subId });
            await del.ExecuteNonQueryAsync();
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }
}
