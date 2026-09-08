using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Http;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// Contracts 4.3 public writes. POST /contact stores the row and the outbox row
// (sql.md 8.13). POST /subscriptions/verify follows sql.md 8.12 verify. POST
// /subscriptions/unsubscribe accepts token from query, JSON, or the RFC 8058
// form-urlencoded body.
public sealed class A12PublicEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A12Host? _host;

    public A12PublicEndpointsTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await A12MeEndpointsTests.MigrateAndCleanAsync(_fixture.ConnectionString);
        _host = await A12Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    // POST /contact

    [Fact]
    public async Task Contact_valid_body_creates_row_and_outbox()
    {
        var response = await PostJsonAsync("/contact",
            "{\"name\":\"Jane\",\"email\":\"jane@example.com\",\"message\":\"Hi there\"}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.RootElement.GetProperty("id").GetInt64() > 0);

        var rows = await CountRowsAsync("contact_message");
        Assert.Equal(1, rows);
        var outbox = await CountOutboxAsync("contact.received");
        Assert.Equal(1, outbox);
    }

    [Fact]
    public async Task Contact_bad_email_is_400_validation_failed()
    {
        var response = await PostJsonAsync("/contact",
            "{\"name\":\"J\",\"email\":\"nope\",\"message\":\"Hi\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Contact_missing_message_is_400_validation_failed()
    {
        var response = await PostJsonAsync("/contact",
            "{\"name\":\"J\",\"email\":\"j@e.com\",\"message\":\"\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Contact_too_long_message_is_400_validation_failed()
    {
        var big = new string('x', 2001);
        var response = await PostJsonAsync("/contact",
            $"{{\"name\":\"J\",\"email\":\"j@e.com\",\"message\":\"{big}\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // POST /subscriptions/verify

    [Fact]
    public async Task Verify_unknown_token_is_404_not_found()
    {
        var token = Keys.SubscribeVerifyPrefix + new string('A', 43);
        var response = await PostJsonAsync("/subscriptions/verify",
            $"{{\"token\":\"{token}\"}}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.NotFound, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Verify_malformed_token_is_404_not_found()
    {
        var response = await PostJsonAsync("/subscriptions/verify", "{\"token\":\"garbage\"}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Verify_expired_unverified_token_is_404()
    {
        var (subscriberId, plaintext) = await SeedSubscriberWithVerifyTokenAsync(
            verified: false, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        _ = subscriberId;
        var response = await PostJsonAsync("/subscriptions/verify", $"{{\"token\":\"{plaintext}\"}}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Verify_unverified_unexpired_sets_verifiedAt_returns_200()
    {
        var (subscriberId, plaintext) = await SeedSubscriberWithVerifyTokenAsync(
            verified: false, expiresAt: DateTimeOffset.UtcNow.AddHours(24));
        var response = await PostJsonAsync("/subscriptions/verify", $"{{\"token\":\"{plaintext}\"}}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.String, body.RootElement.GetProperty("verifiedAt").ValueKind);
        var verifiedAt = await ReadVerifiedAtAsync(subscriberId);
        Assert.NotNull(verifiedAt);
    }

    [Fact]
    public async Task Verify_already_verified_row_returns_200_with_existing_value_regardless_of_expiry()
    {
        var (subscriberId, plaintext) = await SeedSubscriberWithVerifyTokenAsync(
            verified: true, expiresAt: DateTimeOffset.UtcNow.AddYears(-1));
        var response = await PostJsonAsync("/subscriptions/verify", $"{{\"token\":\"{plaintext}\"}}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Second call is idempotent.
        var second = await PostJsonAsync("/subscriptions/verify", $"{{\"token\":\"{plaintext}\"}}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        _ = subscriberId;
    }

    // POST /subscriptions/unsubscribe

    [Fact]
    public async Task Unsubscribe_query_string_204_and_sets_row()
    {
        var subscriberId = await SeedSubscriberAsync(unsubscribed: false);
        var token = await ReadUnsubscribeTokenAsync(subscriberId);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/subscriptions/unsubscribe?token={token}");
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(await ReadUnsubscribedAtAsync(subscriberId));
    }

    [Fact]
    public async Task Unsubscribe_json_body_204_and_sets_row()
    {
        var subscriberId = await SeedSubscriberAsync(unsubscribed: false);
        var token = await ReadUnsubscribeTokenAsync(subscriberId);

        var response = await PostJsonAsync("/subscriptions/unsubscribe", $"{{\"token\":\"{token}\"}}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(await ReadUnsubscribedAtAsync(subscriberId));
    }

    [Fact]
    public async Task Unsubscribe_form_urlencoded_204_when_token_in_query()
    {
        var subscriberId = await SeedSubscriberAsync(unsubscribed: false);
        var token = await ReadUnsubscribeTokenAsync(subscriberId);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/subscriptions/unsubscribe?token={token}");
        req.Content = new StringContent("List-Unsubscribe=One-Click", Encoding.UTF8,
            "application/x-www-form-urlencoded");
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(await ReadUnsubscribedAtAsync(subscriberId));
    }

    [Fact]
    public async Task Unsubscribe_is_idempotent()
    {
        var subscriberId = await SeedSubscriberAsync(unsubscribed: false);
        var token = await ReadUnsubscribeTokenAsync(subscriberId);

        using var first = new HttpRequestMessage(HttpMethod.Post, $"/subscriptions/unsubscribe?token={token}");
        Assert.Equal(HttpStatusCode.NoContent, (await _host!.Client.SendAsync(first)).StatusCode);
        using var second = new HttpRequestMessage(HttpMethod.Post, $"/subscriptions/unsubscribe?token={token}");
        Assert.Equal(HttpStatusCode.NoContent, (await _host!.Client.SendAsync(second)).StatusCode);
    }

    [Fact]
    public async Task Unsubscribe_unknown_token_is_404()
    {
        var token = Keys.SubscribeUnsubscribePrefix + new string('A', 43);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/subscriptions/unsubscribe?token={token}");
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> PostJsonAsync(string path, string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host!.Client.SendAsync(req);
    }

    private async Task<(long subscriberId, string plaintext)> SeedSubscriberWithVerifyTokenAsync(
        bool verified, DateTimeOffset expiresAt)
    {
        var verify = Keys.MintVerifyToken();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        // Requires a person row first.
        await using (var per = new NpgsqlCommand(@"
insert into person (cognito_sub, email) values ($1, $2)
on conflict (cognito_sub) do update set email = excluded.email
returning id;", conn))
        {
            per.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.NewGuid() });
            per.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "seed-" + Guid.NewGuid().ToString("N") + "@example.com" });
            var pid = (long)(await per.ExecuteScalarAsync() ?? 0L);

            await using var sub = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token,
                        verify_token_hash, verify_expires_at, verified_at)
values ($1, 'email', $2, $3, $4, $5, $6) returning id;", conn);
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pid });
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"vt-{Guid.NewGuid():N}@example.com" });
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = Keys.MintUnsubscribeToken().Token });
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = verify.Hash });
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = expiresAt.ToUniversalTime() });
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = verified ? (object)DateTimeOffset.UtcNow : DBNull.Value });
            var sid = (long)(await sub.ExecuteScalarAsync() ?? 0L);
            return (sid, verify.Token);
        }
    }

    private async Task<long> SeedSubscriberAsync(bool unsubscribed)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var per = new NpgsqlCommand(@"
insert into person (cognito_sub, email) values ($1, $2) returning id;", conn))
        {
            per.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.NewGuid() });
            per.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "u-" + Guid.NewGuid().ToString("N") + "@example.com" });
            var pid = (long)(await per.ExecuteScalarAsync() ?? 0L);
            await using var sub = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, unsubscribed_at)
values ($1, 'email', $2, $3, $4) returning id;", conn);
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pid });
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"un-{Guid.NewGuid():N}@example.com" });
            sub.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = Keys.MintUnsubscribeToken().Token });
            sub.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.TimestampTz,
                Value = unsubscribed ? (object)DateTimeOffset.UtcNow : DBNull.Value,
            });
            return (long)(await sub.ExecuteScalarAsync() ?? 0L);
        }
    }

    private async Task<string> ReadUnsubscribeTokenAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select unsubscribe_token from subscriber where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        return (string)(await cmd.ExecuteScalarAsync() ?? "");
    }

    private async Task<DateTimeOffset?> ReadUnsubscribedAtAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select unsubscribed_at from subscriber where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        var r = await cmd.ExecuteScalarAsync();
        return r switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => (DateTimeOffset?)null,
        };
    }

    private async Task<DateTimeOffset?> ReadVerifiedAtAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select verified_at from subscriber where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        var r = await cmd.ExecuteScalarAsync();
        return r switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => (DateTimeOffset?)null,
        };
    }

    private async Task<int> CountRowsAsync(string table)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"select count(*) from {table};", conn);
        return (int)Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<int> CountOutboxAsync(string topic)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from outbox where topic = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = topic });
        return (int)Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
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
