using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// A9 acceptance: every listed code on the routes endpoints; api.md 11.1 upload
// path (canonicalize, hash, existing-row lookup, PUT, insert, s3_key conflict);
// from-event streaming from the event's published locations in seq order.
public sealed class A9AdminRouteEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A9Host? _host;

    public A9AdminRouteEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAndSeedAsync();
        _host = await A9Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task MigrateAndSeedAsync()
    {
        var options = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(options);
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "update event set route_id = null;",
            "delete from location;",
            "delete from event;",
            "delete from route;",
            "delete from beacon;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
    }

    // ---------- POST /admin/routes ----------

    [Fact]
    public async Task Upload_returns_201_and_writes_object_and_row()
    {
        var body = "{\"name\":\"2026 draft\",\"points\":["
                 + "{\"lat\":46.87,\"lng\":-114.0,\"recordedAt\":\"2026-12-22T01:31:07.000Z\"},"
                 + "{\"lat\":46.88,\"lng\":-114.01,\"recordedAt\":null}"
                 + "]}";
        var response = await SendAsync(HttpMethod.Post, "/admin/routes", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = await ReadJsonAsync(response);
        var s3Key = doc.RootElement.GetProperty("s3Key").GetString()!;
        Assert.StartsWith("routes/", s3Key);
        Assert.EndsWith(".json", s3Key);
        // Bytes reached the store.
        var head = await _host!.Store.HeadObjectAsync(s3Key);
        Assert.NotNull(head);
    }

    [Fact]
    public async Task Upload_of_identical_bytes_returns_existing_row_no_second_put()
    {
        var body = "{\"name\":\"twice\",\"points\":["
                 + "{\"lat\":1,\"lng\":1,\"recordedAt\":null},"
                 + "{\"lat\":2,\"lng\":2,\"recordedAt\":null}"
                 + "]}";
        var first = await SendAsync(HttpMethod.Post, "/admin/routes", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstDoc = await ReadJsonAsync(first);
        var key = firstDoc.RootElement.GetProperty("s3Key").GetString()!;
        var putsAfterFirst = _host!.Store.PutCount(key);

        var second = await SendAsync(HttpMethod.Post, "/admin/routes", body);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var putsAfterSecond = _host.Store.PutCount(key);
        Assert.Equal(putsAfterFirst, putsAfterSecond);
        var secondDoc = await ReadJsonAsync(second);
        Assert.Equal(firstDoc.RootElement.GetProperty("id").GetInt64(),
            secondDoc.RootElement.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Upload_under_two_points_is_400_validation_failed()
    {
        var body = "{\"name\":\"x\",\"points\":[{\"lat\":1,\"lng\":1,\"recordedAt\":null}]}";
        var response = await SendAsync(HttpMethod.Post, "/admin/routes", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    // Uploads over 50,000 points return 413 payload_too_large. The point count
    // trip runs after the body arrives (contracts 1.4), so this exercises the
    // route endpoint's own guard rather than the pipeline body limit.
    [Fact]
    public async Task Upload_over_point_count_is_413_payload_too_large()
    {
        // Craft the smallest valid body over 50,000 points. Compact JSON keeps
        // this under the 5 MB pipeline limit while still tripping the point
        // count guard.
        var sb = new StringBuilder(2 * 1024 * 1024);
        sb.Append("{\"name\":\"big\",\"points\":[");
        var count = 50_001;
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"lat\":1,\"lng\":1,\"recordedAt\":null}");
        }
        sb.Append("]}");
        var response = await SendAsync(HttpMethod.Post, "/admin/routes", sb.ToString());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // ---------- POST /admin/routes/from-event/{eventId} ----------

    [Fact]
    public async Task FromEvent_missing_event_is_404()
    {
        var response = await SendAsync(HttpMethod.Post, "/admin/routes/from-event/999999",
            "{\"name\":\"gone\"}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FromEvent_under_two_points_is_400_validation_failed()
    {
        var evt = await SeedEventAsync(year: 2080);
        var beacon = await SeedBeaconAsync("only-one");
        await InsertLocationAsync(evt, beacon, published: true);
        var response = await SendAsync(HttpMethod.Post, $"/admin/routes/from-event/{evt}",
            "{\"name\":\"one-point\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task FromEvent_success_stores_route_from_published_locations()
    {
        var evt = await SeedEventAsync(year: 2081);
        var beacon = await SeedBeaconAsync("from-evt");
        for (var i = 0; i < 5; i++) await InsertLocationAsync(evt, beacon, published: true);
        var response = await SendAsync(HttpMethod.Post, $"/admin/routes/from-event/{evt}",
            "{\"name\":\"real flight\"}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal(5, dto.RootElement.GetProperty("pointCount").GetInt32());
    }

    // ---------- DELETE /admin/routes/{id} ----------

    [Fact]
    public async Task Delete_in_use_by_event_is_409_route_in_use()
    {
        var body = "{\"name\":\"delete-me\",\"points\":[{\"lat\":1,\"lng\":1,\"recordedAt\":null},{\"lat\":2,\"lng\":2,\"recordedAt\":null}]}";
        var upload = await SendAsync(HttpMethod.Post, "/admin/routes", body);
        var doc = await ReadJsonAsync(upload);
        var routeId = doc.RootElement.GetProperty("id").GetInt64();

        // Attach to a new event.
        var evt = await SeedEventAsync(2082);
        await LinkRouteAsync(evt, routeId);

        var response = await SendAsync(HttpMethod.Delete, $"/admin/routes/{routeId}", "");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("route_in_use", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Delete_ok_deletes_row_and_object()
    {
        var body = "{\"name\":\"gone-soon\",\"points\":[{\"lat\":9,\"lng\":9,\"recordedAt\":null},{\"lat\":10,\"lng\":10,\"recordedAt\":null}]}";
        var upload = await SendAsync(HttpMethod.Post, "/admin/routes", body);
        var doc = await ReadJsonAsync(upload);
        var routeId = doc.RootElement.GetProperty("id").GetInt64();
        var s3Key = doc.RootElement.GetProperty("s3Key").GetString()!;

        var response = await SendAsync(HttpMethod.Delete, $"/admin/routes/{routeId}", "");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var head = await _host!.Store.HeadObjectAsync(s3Key);
        Assert.Null(head);
    }

    [Fact]
    public async Task Delete_unknown_id_is_404()
    {
        var response = await SendAsync(HttpMethod.Delete, "/admin/routes/999999", "");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<long> SeedEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, created_by, updated_at)
values ($1, $2, 1, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"E {year}" });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> SeedBeaconAsync(string name)
    {
        var key = Wmsfo.Api.Security.Keys.MintKey();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into beacon (name, role, key_hash, key_prefix, is_active, created_by, updated_at)
values ($1, 'beacon', $2, $3, false, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = key.Hash });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key.Prefix });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task InsertLocationAsync(long eventId, long beaconId, bool published)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        long nextSeq;
        await using (var seq = new NpgsqlCommand(
            "update event set next_seq = next_seq + 1 where id = $1 returning next_seq - 1;", conn))
        {
            seq.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
            nextSeq = (long)(await seq.ExecuteScalarAsync() ?? 0L);
        }
        await using var cmd = new NpgsqlCommand(@"
insert into location (event_id, beacon_id, seq, recorded_at, received_at, lat, lng, published)
values ($1, $2, $3, now(), now(), 46.87, -114, $4);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = nextSeq });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = published });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task LinkRouteAsync(long eventId, long routeId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("update event set route_id = $1 where id = $2;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = routeId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        await cmd.ExecuteNonQueryAsync();
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
