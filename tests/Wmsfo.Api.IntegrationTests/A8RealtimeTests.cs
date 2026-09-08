using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Data;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// A8 acceptance criteria (realtime section): the callback guard on each forwarded
// header; a rotated key rejected at the message path; the authorize branches of
// contracts 2.4; the message-path branches of contracts 2.5.
public sealed class A8RealtimeTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A8Host? _host;

    public A8RealtimeTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAsync();
        _host = await A8Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task MigrateAsync()
    {
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(contextOptions);
        await db.Database.MigrateAsync();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from location;",
            "delete from beacon_enrollment_token;",
            "delete from beacon;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from event;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ---------- The forwarded-header guard fires on each of the three names. ----------

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("X-Forwarded-Host", "example.com")]
    [InlineData("X-Forwarded-Proto", "https")]
    public async Task Authorize_with_forwarded_header_answers_404_empty(string header, string value)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/realtime/authorize")
        {
            Content = new StringContent(
                "{\"channel\":\"wmsfo-api-test:location\",\"connectionId\":\"c1\"}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(header, value);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Empty(body);
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("X-Forwarded-Host", "example.com")]
    [InlineData("X-Forwarded-Proto", "https")]
    public async Task Message_with_forwarded_header_answers_404_empty(string header, string value)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/realtime/message")
        {
            Content = new StringContent(
                "{\"channel\":\"wmsfo-api-test:ingest\",\"event\":\"location\",\"data\":{},\"connectionId\":\"c1\",\"identity\":\"1:1\"}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(header, value);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Empty(body);
    }

    // ---------- Authorize branches (contracts 2.4). ----------

    [Fact]
    public async Task Authorize_public_topic_allows_without_credential()
    {
        var response = await _host!.Client.PostAsync("/realtime/authorize",
            new StringContent("{\"channel\":\"wmsfo-api-test:location\",\"connectionId\":\"c1\"}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("allow").GetBoolean());
    }

    [Fact]
    public async Task Authorize_wrong_service_prefix_denies()
    {
        var response = await _host!.Client.PostAsync("/realtime/authorize",
            new StringContent("{\"channel\":\"someone-else:location\",\"connectionId\":\"c1\"}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("allow").GetBoolean());
    }

    [Fact]
    public async Task Authorize_malformed_channel_denies()
    {
        var response = await _host!.Client.PostAsync("/realtime/authorize",
            new StringContent("{\"channel\":\"\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("allow").GetBoolean());
    }

    [Fact]
    public async Task Authorize_ingest_valid_key_allows_with_identity()
    {
        var beacon = await SeedBeaconAsync("ingest", role: "beacon");
        var key = _keys[beacon];

        var response = await _host!.Client.PostAsync("/realtime/authorize",
            new StringContent(
                $"{{\"channel\":\"wmsfo-api-test:ingest\",\"credential\":\"{key}\",\"connectionId\":\"c1\"}}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("allow").GetBoolean());
        Assert.Equal($"{beacon}:1", body.RootElement.GetProperty("identity").GetString());
    }

    [Fact]
    public async Task Authorize_ingest_revoked_key_denies()
    {
        var beacon = await SeedBeaconAsync("rev", role: "beacon");
        var key = _keys[beacon];
        await RevokeBeaconAsync(beacon);

        var response = await _host!.Client.PostAsync("/realtime/authorize",
            new StringContent(
                $"{{\"channel\":\"wmsfo-api-test:ingest\",\"credential\":\"{key}\",\"connectionId\":\"c1\"}}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("allow").GetBoolean());
    }

    [Fact]
    public async Task Authorize_ingest_bad_key_format_denies()
    {
        var response = await _host!.Client.PostAsync("/realtime/authorize",
            new StringContent(
                "{\"channel\":\"wmsfo-api-test:ingest\",\"credential\":\"not-a-key\",\"connectionId\":\"c1\"}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("allow").GetBoolean());
    }

    // ---------- Message-path (contracts 2.5). ----------

    [Fact]
    public async Task Message_wrong_channel_is_403_forbidden()
    {
        var response = await _host!.Client.PostAsync("/realtime/message",
            new StringContent(
                "{\"channel\":\"wmsfo-api-test:location\",\"event\":\"location\",\"data\":{},\"connectionId\":\"c1\",\"identity\":\"1:1\"}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Message_malformed_identity_is_403_forbidden()
    {
        await SeedEventAsync(statusId: 3);
        var response = await _host!.Client.PostAsync("/realtime/message",
            new StringContent(
                "{\"channel\":\"wmsfo-api-test:ingest\",\"event\":\"location\",\"data\":{\"lat\":0,\"lng\":0,\"recordedAt\":\"2026-12-22T01:31:07Z\"},\"connectionId\":\"c1\",\"identity\":\"garbage\"}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Message_rotated_key_version_rejected()
    {
        await SeedEventAsync(statusId: 3);
        var beacon = await SeedBeaconAsync("rotate", role: "beacon", isActive: true);
        // Rotate the beacon so key_version becomes 2.
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var upd = new NpgsqlCommand(
                "update beacon set key_version = key_version + 1 where id = $1;", conn);
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beacon });
            await upd.ExecuteNonQueryAsync();
        }

        // Send a message with the old identity (:1).
        var body = "{\"channel\":\"wmsfo-api-test:ingest\",\"event\":\"location\","
                 + "\"data\":{\"lat\":0,\"lng\":0,\"recordedAt\":\"2026-12-22T01:31:07Z\"},"
                 + $"\"connectionId\":\"c1\",\"identity\":\"{beacon}:1\"}}";
        var response = await _host!.Client.PostAsync("/realtime/message",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Message_revoked_beacon_is_403_forbidden()
    {
        await SeedEventAsync(statusId: 3);
        var beacon = await SeedBeaconAsync("revmsg", role: "beacon", isActive: true);
        await RevokeBeaconAsync(beacon);

        var body = "{\"channel\":\"wmsfo-api-test:ingest\",\"event\":\"location\","
                 + "\"data\":{\"lat\":0,\"lng\":0,\"recordedAt\":\"2026-12-22T01:31:07Z\"},"
                 + $"\"connectionId\":\"c1\",\"identity\":\"{beacon}:1\"}}";
        var response = await _host!.Client.PostAsync("/realtime/message",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Message_location_event_returns_seq_and_published()
    {
        await SeedEventAsync(statusId: 3);
        var beacon = await SeedBeaconAsync("msg-active", role: "beacon", isActive: true);

        var body = "{\"channel\":\"wmsfo-api-test:ingest\",\"event\":\"location\","
                 + "\"data\":{\"lat\":46.87,\"lng\":-114,\"recordedAt\":\"2026-12-22T01:31:07Z\"},"
                 + $"\"connectionId\":\"c1\",\"identity\":\"{beacon}:1\"}}";
        var response = await _host!.Client.PostAsync("/realtime/message",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("published").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("seq").GetInt64() > 0);
    }

    [Fact]
    public async Task Message_unknown_event_is_400_validation_failed()
    {
        await SeedEventAsync(statusId: 3);
        var beacon = await SeedBeaconAsync("msg-badevent", role: "beacon", isActive: true);
        var body = "{\"channel\":\"wmsfo-api-test:ingest\",\"event\":\"heartbeat\","
                 + "\"data\":{},"
                 + $"\"connectionId\":\"c1\",\"identity\":\"{beacon}:1\"}}";
        var response = await _host!.Client.PostAsync("/realtime/message",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.ValidationFailed, doc.RootElement.GetProperty("code").GetString());
    }

    // ---------- helpers ----------

    private static readonly ConcurrentDictionary<long, string> _keys = new();

    private async Task<long> SeedEventAsync(int statusId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var wipe = new NpgsqlCommand("delete from event where status_id = 3;", conn))
        {
            await wipe.ExecuteNonQueryAsync();
        }
        var year = 2000 + Random.Shared.Next(1, 90);
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, created_by, updated_at)
values ($1, $2, $3, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (short)statusId });
        var id = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        return id;
    }

    private async Task<long> SeedBeaconAsync(string name, string role, bool isActive = false)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        if (isActive)
        {
            await using var wipe = new NpgsqlCommand("update beacon set is_active = false where is_active;", conn);
            await wipe.ExecuteNonQueryAsync();
        }
        var minted = Wmsfo.Api.Security.Keys.MintKey();
        await using var cmd = new NpgsqlCommand(@"
insert into beacon (name, role, key_hash, key_prefix, is_active, created_by, updated_at)
values ($1, $2, $3, $4, $5, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = role });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = minted.Hash });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = minted.Prefix });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = isActive });
        var id = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        _keys[id] = minted.Token;
        return id;
    }

    private async Task RevokeBeaconAsync(long beaconId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "update beacon set revoked_at = now(), is_active = false where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
        await cmd.ExecuteNonQueryAsync();
    }
}
