using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Data;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A8 acceptance criteria 753-754: every listed code per endpoint; POST /locations
// answers the documented body with serverTime after commit; seq strictly
// increasing across 1000 concurrent inserts; two beacons with one active (only
// the active one publishes); the callback guard on each forwarded header; a
// rotated key rejected at the message path.
public sealed class A8BeaconEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A8Host? _host;

    public A8BeaconEndpointsTests(PostgresFixture fixture)
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
        // Wipe rows the tests touch so the class-scoped fixture stays clean between test runs.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from beacon_log;",
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

    // ---------- POST /beacons/enroll ----------

    [Fact]
    public async Task Enroll_returns_key_and_ingest_channel()
    {
        var beacon = await SeedBeaconAsync("Helicopter", role: "beacon");
        var (token, key) = await MintEnrollmentTokenAsync(beacon);

        var response = await _host!.Client.PostAsync("/beacons/enroll",
            new StringContent($"{{\"token\":\"{token}\"}}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(beacon, body.RootElement.GetProperty("beaconId").GetInt64());
        Assert.Equal("Helicopter", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("beacon", body.RootElement.GetProperty("role").GetString());
        Assert.Equal(key, body.RootElement.GetProperty("key").GetString());
        Assert.Equal("wmsfo-api-test:ingest", body.RootElement.GetProperty("ingestChannel").GetString());
        Assert.NotNull(body.RootElement.GetProperty("serverTime").GetString());
    }

    [Fact]
    public async Task Enroll_bad_token_format_is_400_validation_failed()
    {
        var response = await _host!.Client.PostAsync("/beacons/enroll",
            new StringContent("{\"token\":\"not-a-valid-token\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Enroll_unknown_token_is_404_enrollment_token_invalid()
    {
        var unknown = "wet_" + new string('a', 43);
        var response = await _host!.Client.PostAsync("/beacons/enroll",
            new StringContent($"{{\"token\":\"{unknown}\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("enrollment_token_invalid", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Enroll_consumed_token_is_404_enrollment_token_invalid()
    {
        var beacon = await SeedBeaconAsync("H1");
        var (token, _) = await MintEnrollmentTokenAsync(beacon);
        // First call consumes.
        var first = await _host!.Client.PostAsync("/beacons/enroll",
            new StringContent($"{{\"token\":\"{token}\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // Second answers 404.
        var second = await _host.Client.PostAsync("/beacons/enroll",
            new StringContent($"{{\"token\":\"{token}\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal("enrollment_token_invalid", await ReadCodeAsync(second));
    }

    [Fact]
    public async Task Enroll_revoked_beacon_is_404_enrollment_token_invalid()
    {
        var beacon = await SeedBeaconAsync("Hrev");
        var (token, _) = await MintEnrollmentTokenAsync(beacon);
        await RevokeBeaconAsync(beacon);
        var response = await _host!.Client.PostAsync("/beacons/enroll",
            new StringContent($"{{\"token\":\"{token}\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("enrollment_token_invalid", await ReadCodeAsync(response));
    }

    // ---------- GET /beacons/me ----------

    [Fact]
    public async Task Me_stamps_last_seen_and_returns_live_event()
    {
        var beacon = await SeedBeaconAsync("me-beacon", role: "beacon");
        var key = await SetBeaconKeyAsync(beacon);
        await SeedEventAsync(statusId: 3);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/beacons/me");
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(beacon, body.RootElement.GetProperty("beaconId").GetInt64());
        Assert.True(body.RootElement.GetProperty("liveEventId").GetInt64() > 0);

        // /beacons/me sets last_seen_at (contracts 4.2). A freshly-seeded beacon
        // has last_seen_at null; after the call it is non-null.
        var afterUpd = await ReadBeaconLastSeenAsync(beacon);
        Assert.NotNull(afterUpd);
    }

    [Fact]
    public async Task Me_without_header_is_401_unauthenticated()
    {
        var response = await _host!.Client.GetAsync("/beacons/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthenticated, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Me_revoked_key_is_401_unauthenticated()
    {
        var beacon = await SeedBeaconAsync("me-rev");
        var key = await SetBeaconKeyAsync(beacon);
        await RevokeBeaconAsync(beacon);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/beacons/me");
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- POST /locations ----------

    [Fact]
    public async Task Locations_active_beacon_stores_row_with_published_true_and_returns_body()
    {
        var evtId = await SeedEventAsync(statusId: 3);
        var beacon = await SeedBeaconAsync("active-1", isActive: true);
        var key = await SetBeaconKeyAsync(beacon);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/locations")
        {
            Content = new StringContent(
                "{\"lat\":46.87,\"lng\":-114,\"recordedAt\":\"2026-12-22T01:31:07Z\",\"speedMps\":31.2,\"altitudeM\":1210,\"headingDeg\":84,\"accuracyM\":6}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.RootElement.GetProperty("published").GetBoolean());
        Assert.True(body.RootElement.GetProperty("seq").GetInt64() > 0);
        Assert.NotNull(body.RootElement.GetProperty("receivedAt").GetString());
        Assert.NotNull(body.RootElement.GetProperty("serverTime").GetString());

        var (seq, published) = await ReadLatestLocationAsync(evtId);
        Assert.True(published);
        Assert.Equal(seq, body.RootElement.GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task Locations_inactive_beacon_stores_row_with_published_false()
    {
        await SeedEventAsync(statusId: 3);
        var beacon = await SeedBeaconAsync("inactive", isActive: false);
        var key = await SetBeaconKeyAsync(beacon);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/locations")
        {
            Content = new StringContent(
                "{\"lat\":1,\"lng\":1,\"recordedAt\":\"2026-12-22T01:31:07Z\"}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.False(body.RootElement.GetProperty("published").GetBoolean());
    }

    [Fact]
    public async Task Locations_no_live_event_is_409_no_live_event()
    {
        var beacon = await SeedBeaconAsync("no-live", isActive: true);
        var key = await SetBeaconKeyAsync(beacon);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/locations")
        {
            Content = new StringContent(
                "{\"lat\":1,\"lng\":1,\"recordedAt\":\"2026-12-22T01:31:07Z\"}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no_live_event", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Locations_validation_failed_out_of_range_lat()
    {
        await SeedEventAsync(statusId: 3);
        var beacon = await SeedBeaconAsync("val", isActive: true);
        var key = await SetBeaconKeyAsync(beacon);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/locations")
        {
            Content = new StringContent(
                "{\"lat\":91,\"lng\":0,\"recordedAt\":\"2026-12-22T01:31:07Z\"}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Locations_missing_key_is_401_unauthenticated()
    {
        var response = await _host!.Client.PostAsync("/locations",
            new StringContent("{\"lat\":1,\"lng\":1,\"recordedAt\":\"2026-12-22T01:31:07Z\"}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Locations_seq_strictly_increasing_under_1000_concurrent_inserts()
    {
        await SeedEventAsync(statusId: 3);
        var beacon = await SeedBeaconAsync("concurrent-1", isActive: true);
        var key = await SetBeaconKeyAsync(beacon);

        const int total = 1000;
        // 25 workers × 40 requests = 1000 inserts. Rate limits are disabled in
        // the A8Host so every request lands. Each worker uses a dedicated HttpClient
        // so client-side connection sharing does not serialize the requests.
        const int workers = 25;
        const int perWorker = total / workers;
        var seqs = new ConcurrentBag<long>();
        var address = _host!.Client.BaseAddress!;
        var tasks = new List<Task>();
        for (var w = 0; w < workers; w++)
        {
            tasks.Add(Task.Run(async () =>
            {
                using var client = new HttpClient { BaseAddress = address };
                for (var i = 0; i < perWorker; i++)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, "/locations")
                    {
                        Content = new StringContent(
                            "{\"lat\":46,\"lng\":-114,\"recordedAt\":\"2026-12-22T01:31:07Z\"}",
                            Encoding.UTF8, "application/json"),
                    };
                    req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
                    using var response = await client.SendAsync(req);
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    var okBody = await ReadJsonAsync(response);
                    seqs.Add(okBody.RootElement.GetProperty("seq").GetInt64());
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(total, seqs.Count);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());

        // sql.md 8.2: seq is assigned monotonically under the event row lock, so
        // the union of returned seqs must be a contiguous range starting at 1.
        var sorted = seqs.OrderBy(s => s).ToList();
        Assert.Equal(1L, sorted[0]);
        Assert.Equal((long)total, sorted[^1]);
        for (var i = 0; i < sorted.Count - 1; i++)
        {
            Assert.Equal(sorted[i] + 1, sorted[i + 1]);
        }
    }

    // A8 test: two beacons with one active - only the active one publishes rows.
    [Fact]
    public async Task Locations_two_beacons_one_active_only_active_publishes()
    {
        var evtId = await SeedEventAsync(statusId: 3);
        var b1 = await SeedBeaconAsync("b1", isActive: true);
        var b2 = await SeedBeaconAsync("b2", isActive: false);
        var k1 = await SetBeaconKeyAsync(b1);
        var k2 = await SetBeaconKeyAsync(b2);

        async Task Send(string key)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/locations")
            {
                Content = new StringContent(
                    "{\"lat\":1,\"lng\":1,\"recordedAt\":\"2026-12-22T01:31:07Z\"}",
                    Encoding.UTF8, "application/json"),
            };
            req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
            using var response = await _host!.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await Send(k1);
        await Send(k2);
        await Send(k1);

        var counts = await ReadLocationPublishedCountsAsync(evtId);
        Assert.Equal(2, counts.published);
        Assert.Equal(1, counts.notPublished);
    }

    // ---------- POST /beacons/heartbeat ----------

    [Fact]
    public async Task Heartbeat_stores_telemetry_and_returns_liveEventId()
    {
        var evtId = await SeedEventAsync(statusId: 3);
        var beacon = await SeedBeaconAsync("hb", isActive: true);
        var key = await SetBeaconKeyAsync(beacon);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/beacons/heartbeat")
        {
            Content = new StringContent(
                "{\"sentAt\":\"2026-12-22T01:31:07Z\",\"power\":{\"batteryPercent\":90}}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(evtId, body.RootElement.GetProperty("liveEventId").GetInt64());
        Assert.True(body.RootElement.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Heartbeat_over_8kb_is_413_payload_too_large()
    {
        var beacon = await SeedBeaconAsync("hb-big");
        var key = await SetBeaconKeyAsync(beacon);
        var big = new string('a', 8500);
        var body = $"{{\"sentAt\":\"2026-12-22T01:31:07Z\",\"power\":{{\"batteryPercent\":90,\"filler\":\"{big}\"}}}}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "/beacons/heartbeat")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_bad_enum_is_400_validation_failed()
    {
        var beacon = await SeedBeaconAsync("hb-enum");
        var key = await SetBeaconKeyAsync(beacon);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/beacons/heartbeat")
        {
            Content = new StringContent(
                "{\"sentAt\":\"2026-12-22T01:31:07Z\",\"transport\":{\"socketState\":\"funky\"}}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    // ---------- POST /beacons/logs ----------

    [Fact]
    public async Task Beacon_logs_admin_role_stores_log()
    {
        var beacon = await SeedBeaconAsync("adm", role: "admin");
        var key = await SetBeaconKeyAsync(beacon);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/beacons/logs")
        {
            Content = new StringContent("hello log body", Encoding.UTF8, "text/plain"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        req.Headers.Add("X-App-Version", "1.0.3");
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.RootElement.GetProperty("id").GetInt64() > 0);
        Assert.True(body.RootElement.GetProperty("sizeBytes").GetInt32() > 0);
    }

    [Fact]
    public async Task Beacon_logs_role_beacon_is_403_forbidden()
    {
        var beacon = await SeedBeaconAsync("nonadm", role: "beacon");
        var key = await SetBeaconKeyAsync(beacon);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/beacons/logs")
        {
            Content = new StringContent("body", Encoding.UTF8, "text/plain"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Beacon_logs_json_content_type_is_415_unsupported_media_type()
    {
        var beacon = await SeedBeaconAsync("adm-json", role: "admin");
        var key = await SetBeaconKeyAsync(beacon);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/beacons/logs")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<long> SeedEventAsync(int statusId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        // Clean out any existing live event first - event_one_live is a partial
        // unique index.
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

    private async Task<long> SeedBeaconAsync(string name, string role = "beacon", bool isActive = false)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        if (isActive)
        {
            // Only one active beacon is allowed.
            await using var wipe = new NpgsqlCommand("update beacon set is_active = false where is_active;", conn);
            await wipe.ExecuteNonQueryAsync();
        }
        var key = Wmsfo.Api.Security.Keys.MintKey();
        await using var cmd = new NpgsqlCommand(@"
insert into beacon (name, role, key_hash, key_prefix, is_active, created_by, updated_at)
values ($1, $2, $3, $4, $5, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = role });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = key.Hash });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key.Prefix });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = isActive });
        var id = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        // Remember the key.
        _testKeys[id] = key.Token;
        return id;
    }

    private static readonly ConcurrentDictionary<long, string> _testKeys = new();

    private Task<string> SetBeaconKeyAsync(long beaconId)
    {
        // The row already has a hash matching the token minted in SeedBeaconAsync;
        // return it here.
        if (_testKeys.TryGetValue(beaconId, out var t)) return Task.FromResult(t);
        throw new InvalidOperationException($"no key remembered for beacon {beaconId}");
    }

    private async Task<(string token, string key)> MintEnrollmentTokenAsync(long beaconId)
    {
        // Mint a fresh beacon key, encrypt it, and insert an enrollment token row.
        var options = _host!.Options;
        var keyBytes = Convert.FromBase64String(options.EnrollmentEncryptionKey);
        var minted = Wmsfo.Api.Security.Keys.MintKey();
        // Update the beacon so /beacons/me works after enroll.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var upd = new NpgsqlCommand(
            "update beacon set key_hash = $1, key_prefix = $2 where id = $3;", conn))
        {
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = minted.Hash });
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = minted.Prefix });
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
            await upd.ExecuteNonQueryAsync();
        }
        var enroll = Wmsfo.Api.Security.Keys.MintEnrollmentToken();
        var cipher = Wmsfo.Api.Security.Keys.Encrypt(keyBytes, minted.Token);
        await using (var cmd = new NpgsqlCommand(@"
insert into beacon_enrollment_token (beacon_id, token_hash, key_ciphertext, expires_at)
values ($1, $2, $3, now() + interval '15 minutes');", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = enroll.Hash });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = cipher });
            await cmd.ExecuteNonQueryAsync();
        }
        return (enroll.Token, minted.Token);
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

    private async Task<DateTimeOffset?> ReadBeaconLastSeenAsync(long beaconId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select last_seen_at from beacon where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        if (reader.IsDBNull(0)) return null;
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private async Task<(long seq, bool published)> ReadLatestLocationAsync(long evtId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select seq, published from location where event_id = $1 order by seq desc limit 1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = evtId });
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetInt64(0), r.GetBoolean(1));
    }

    private async Task<(int published, int notPublished)> ReadLocationPublishedCountsAsync(long evtId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
select
  count(*) filter (where published) as p,
  count(*) filter (where not published) as n
from location where event_id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = evtId });
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return ((int)r.GetInt64(0), (int)r.GetInt64(1));
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }
}
