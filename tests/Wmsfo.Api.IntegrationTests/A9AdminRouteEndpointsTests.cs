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
            "delete from location;",
            "delete from event;",
            "delete from route;",
            "delete from beacon;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SiteRouteSeed.ClearAsync(conn);
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

    // A113: while the site settings draft or the newest published version
    // names the route, the delete is 409 route_in_use with details.where and
    // the impact preview is blocked; the route row and object stay. The
    // route DTOs flag the two.
    [Theory]
    [InlineData(true, false, new[] { "draft" })]
    [InlineData(false, true, new[] { "published" })]
    [InlineData(true, true, new[] { "draft", "published" })]
    public async Task Delete_of_the_site_route_is_409_route_in_use(bool draft, bool published, string[] where)
    {
        var body = "{\"name\":\"site\",\"points\":[{\"lat\":1,\"lng\":1,\"recordedAt\":null},{\"lat\":2,\"lng\":2,\"recordedAt\":null}]}";
        var upload = await SendAsync(HttpMethod.Post, "/admin/routes", body);
        var doc = await ReadJsonAsync(upload);
        var routeId = doc.RootElement.GetProperty("id").GetInt64();
        var s3Key = doc.RootElement.GetProperty("s3Key").GetString()!;
        Assert.False(doc.RootElement.GetProperty("isSiteRoute").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("isDraftRoute").GetBoolean());

        if (draft) await SiteRouteSeed.SetDraftAsync(_fixture.ConnectionString, SiteRouteSeed.Route(routeId));
        if (published) await SiteRouteSeed.PublishAsync(_fixture.ConnectionString, SiteRouteSeed.Route(routeId));

        using (var got = await ReadJsonAsync(await SendAsync(HttpMethod.Get, $"/admin/routes/{routeId}", "")))
        {
            Assert.Equal(published, got.RootElement.GetProperty("isSiteRoute").GetBoolean());
            Assert.Equal(draft, got.RootElement.GetProperty("isDraftRoute").GetBoolean());
        }
        using (var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/admin/routes", "")))
        {
            var item = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(published, item.GetProperty("isSiteRoute").GetBoolean());
            Assert.Equal(draft, item.GetProperty("isDraftRoute").GetBoolean());
        }

        var preview = await SendAsync(HttpMethod.Get, $"/admin/routes/{routeId}/impact", "");
        preview.EnsureSuccessStatusCode();
        using (var impact = await ReadJsonAsync(preview))
        {
            Assert.Equal("This is the site route. Choose another route or None in Site settings first.",
                impact.RootElement.GetProperty("blocked").GetString());
            Assert.Empty(impact.RootElement.GetProperty("unlinks").EnumerateArray());
        }

        var response = await SendAsync(HttpMethod.Delete, $"/admin/routes/{routeId}", "");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using (var error = await ReadJsonAsync(response))
        {
            Assert.Equal("route_in_use", error.RootElement.GetProperty("code").GetString());
            Assert.Equal(where, error.RootElement.GetProperty("details").GetProperty("where")
                .EnumerateArray().Select(w => w.GetString()!).ToArray());
        }
        Assert.NotNull(await _host!.Store.HeadObjectAsync(s3Key));
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var check = new NpgsqlCommand("select count(*) from route where id = $1;", conn);
            check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = routeId });
            Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);
        }

        // Once neither names it (the draft names another route, a newer
        // version names none), the delete goes through.
        await SiteRouteSeed.SetDraftAsync(_fixture.ConnectionString, SiteRouteSeed.Route(routeId + 1000));
        await SiteRouteSeed.PublishAsync(_fixture.ConnectionString, SiteRouteSeed.Route(null));
        using (var impact = await ReadJsonAsync(await SendAsync(HttpMethod.Get, $"/admin/routes/{routeId}/impact", "")))
            Assert.Equal(JsonValueKind.Null, impact.RootElement.GetProperty("blocked").ValueKind);
        var deleted = await SendAsync(HttpMethod.Delete, $"/admin/routes/{routeId}", "");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(await _host.Store.HeadObjectAsync(s3Key));
    }

    [Fact]
    public async Task Delete_ok_deletes_row_and_object()
    {
        var body = "{\"name\":\"gone-soon\",\"points\":[{\"lat\":9,\"lng\":9,\"recordedAt\":null},{\"lat\":10,\"lng\":10,\"recordedAt\":null}]}";
        var upload = await SendAsync(HttpMethod.Post, "/admin/routes", body);
        var doc = await ReadJsonAsync(upload);
        var routeId = doc.RootElement.GetProperty("id").GetInt64();
        var s3Key = doc.RootElement.GetProperty("s3Key").GetString()!;

        // An older published version naming the route does not block: only
        // the newest one is what the site shows.
        await SiteRouteSeed.PublishAsync(_fixture.ConnectionString, SiteRouteSeed.Route(routeId));
        await SiteRouteSeed.PublishAsync(_fixture.ConnectionString, null);

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
insert into beacon (name, key_hash, key_prefix, is_active, created_by, updated_at)
values ($1, $2, $3, false, 'seed', now()) returning id;", conn);
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
        // A38: unique (event_id, lat, lng), so vary lat by the seq.
        var lat = 46.87 + nextSeq * 0.0001;
        await using var cmd = new NpgsqlCommand(@"
insert into location (event_id, beacon_id, seq, recorded_at, received_at, lat, lng, published)
values ($1, $2, $3, now(), now(), $4, -114, $5);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = nextSeq });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = lat });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = published });
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
