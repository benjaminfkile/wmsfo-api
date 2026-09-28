using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// A63 acceptance: PATCH /admin/events/{id} posterLayout (contracts 4.5 Events).
//   - a JSON object round-trips byte-equal through the canonical serializer
//   - null clears it; absent leaves it
//   - an array, a scalar, and an object over 32 KB are 400 on posterLayout
//   - the write is not snapshot-affecting and the snapshot never carries it
//   - the audit row records the write
public sealed class A63PosterLayoutTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Sentinel = "posterLayoutSentinel63";

    private readonly PostgresFixture _fixture;
    private A9Host? _host;

    public A63PosterLayoutTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(contextOptions))
        {
            await db.Database.MigrateAsync();
        }

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from location;",
            "delete from cookie;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from event;",
            "delete from outbox;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
        _host = await A9Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private static string SampleLayout() =>
        "{\"version\":1,\"canvas\":{\"height\":1800,\"width\":1200},\"items\":[" +
        "{\"id\":\"title\",\"kind\":\"text\",\"text\":\"" + Sentinel + " \\u00e9 <b>\",\"x\":0.5,\"y\":-12}," +
        "{\"id\":\"map\",\"kind\":\"image\",\"scale\":1.25,\"visible\":true,\"z\":null}]}";

    [Fact]
    public async Task Patch_object_round_trips_byte_equal_through_the_canonical_serializer()
    {
        var id = await CreateEventAsync(2201);
        var sent = SampleLayout();
        using var sentDoc = JsonDocument.Parse(sent);
        var expected = CanonicalJson.SerializeOpaqueToUtf8Bytes(sentDoc.RootElement);

        var patch = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"posterLayout\":" + sent + "}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using (var patched = await ReadJsonAsync(patch))
        {
            Assert.Equal(expected, CanonicalJson.SerializeToUtf8Bytes(patched.RootElement.GetProperty("posterLayout")));
        }

        using var got = await GetEventAsync(id);
        Assert.Equal(expected, CanonicalJson.SerializeToUtf8Bytes(got.RootElement.GetProperty("posterLayout")));

        // The list read carries it too.
        using var list = await ReadJsonAsync(await SendAdminAsync(HttpMethod.Get, "/admin/events", ""));
        var row = list.RootElement.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("id").GetInt64() == id);
        Assert.Equal(expected, CanonicalJson.SerializeToUtf8Bytes(row.GetProperty("posterLayout")));
    }

    [Fact]
    public async Task Canonical_form_orders_properties_at_every_depth()
    {
        var id = await CreateEventAsync(2202);
        var patch = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}",
            "{\"posterLayout\":{\"zeta\":1,\"alpha\":{\"b\":[{\"y\":2,\"x\":1}],\"a\":\"s\"}}}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var got = await GetEventAsync(id);
        Assert.Equal("{\"alpha\":{\"a\":\"s\",\"b\":[{\"x\":1,\"y\":2}]},\"zeta\":1}",
            Encoding.UTF8.GetString(CanonicalJson.SerializeToUtf8Bytes(got.RootElement.GetProperty("posterLayout"))));
    }

    [Fact]
    public async Task New_event_has_null_layout_absent_leaves_it_and_null_clears_it()
    {
        var id = await CreateEventAsync(2203);
        using (var fresh = await GetEventAsync(id))
        {
            Assert.Equal(JsonValueKind.Null, fresh.RootElement.GetProperty("posterLayout").ValueKind);
        }

        Assert.Equal(HttpStatusCode.OK,
            (await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"posterLayout\":{\"a\":1}}")).StatusCode);

        // A PATCH without the key leaves the layout as it was.
        var rename = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"name\":\"Renamed\"}");
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        using (var kept = await GetEventAsync(id))
        {
            Assert.Equal("Renamed", kept.RootElement.GetProperty("name").GetString());
            Assert.Equal("{\"a\":1}", kept.RootElement.GetProperty("posterLayout").GetRawText());
        }

        var clear = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"posterLayout\":null}");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using (var cleared = await GetEventAsync(id))
        {
            Assert.Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("posterLayout").ValueKind);
        }
        Assert.True(await IsLayoutNullInDbAsync(id));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"a\":1}]")]
    [InlineData("\"layout\"")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task Non_object_is_400_on_posterLayout(string value)
    {
        var id = await CreateEventAsync(2204);
        var response = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"posterLayout\":" + value + "}");
        await AssertValidationFailedOnPosterLayoutAsync(response);
        Assert.True(await IsLayoutNullInDbAsync(id));
    }

    [Fact]
    public async Task Object_over_32_kb_canonical_is_400_and_exactly_32_kb_is_accepted()
    {
        var id = await CreateEventAsync(2205);

        // {"p":"<n characters>"} is n + 8 bytes canonical.
        static string LayoutOfSize(int bytes) => "{\"p\":\"" + new string('x', bytes - 8) + "\"}";

        var over = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"posterLayout\":" + LayoutOfSize(32 * 1024 + 1) + "}");
        await AssertValidationFailedOnPosterLayoutAsync(over);
        Assert.True(await IsLayoutNullInDbAsync(id));

        var atCap = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"posterLayout\":" + LayoutOfSize(32 * 1024) + "}");
        Assert.Equal(HttpStatusCode.OK, atCap.StatusCode);

        // Whitespace in the request does not count; the canonical form does.
        var padded = "{ \"p\" : \"" + new string('x', 32 * 1024 - 8) + "\"" + new string(' ', 4000) + "}";
        var paddedResponse = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"posterLayout\":" + padded + "}");
        Assert.Equal(HttpStatusCode.OK, paddedResponse.StatusCode);
    }

    [Fact]
    public async Task Layout_write_is_not_snapshot_affecting_and_never_in_the_snapshot()
    {
        var id = await CreateEventAsync(2206);
        await RunSqlAsync($"update event set is_current = false; update event set is_current = true where id = {id};");

        // A snapshot-affecting write builds a snapshot that carries the current event.
        var baseline = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"name\":\"Poster Flyover\"}");
        Assert.Equal(HttpStatusCode.OK, baseline.StatusCode);
        var (versionBefore, keyBefore) = await ReadSnapshotRowAsync();
        var bytesBefore = await ReadSnapshotBytesAsync(keyBefore);
        Assert.Contains("Poster Flyover", Encoding.UTF8.GetString(bytesBefore));

        // A layout-only PATCH does not rebuild the snapshot.
        var layoutOnly = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"posterLayout\":" + SampleLayout() + "}");
        Assert.Equal(HttpStatusCode.OK, layoutOnly.StatusCode);
        var (versionAfterLayout, keyAfterLayout) = await ReadSnapshotRowAsync();
        Assert.Equal(versionBefore, versionAfterLayout);
        Assert.Equal(keyBefore, keyAfterLayout);

        // A PATCH that also carries a snapshot field rebuilds, and the rebuilt
        // snapshot hashes the same as before: the layout is not in it.
        var mixed = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}",
            "{\"name\":\"Poster Flyover\",\"posterLayout\":{\"other\":\"" + Sentinel + "\"}}");
        Assert.Equal(HttpStatusCode.OK, mixed.StatusCode);
        var (versionAfterMixed, keyAfterMixed) = await ReadSnapshotRowAsync();
        Assert.True(versionAfterMixed > versionBefore);
        Assert.Equal(keyBefore, keyAfterMixed);

        var snapshotText = Encoding.UTF8.GetString(await ReadSnapshotBytesAsync(keyAfterMixed));
        Assert.DoesNotContain("posterLayout", snapshotText, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, snapshotText, StringComparison.Ordinal);

        // The layout is stored all the same.
        using var got = await GetEventAsync(id);
        Assert.Equal("{\"other\":\"" + Sentinel + "\"}", got.RootElement.GetProperty("posterLayout").GetRawText());
    }

    [Fact]
    public async Task Layout_write_is_audited_with_before_and_after()
    {
        var id = await CreateEventAsync(2207);
        var response = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}", "{\"posterLayout\":{\"a\":\"" + Sentinel + "\"}}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var doc = await ReadJsonAsync(response))
        {
            Assert.Equal("update", doc.RootElement.GetProperty("audit").GetProperty("action").GetString());
        }

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
select action, before::text, after::text from audit_log
where entity = 'event' and entity_id = $1 order by id desc limit 1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("update", reader.GetString(0));
        Assert.DoesNotContain(Sentinel, reader.GetString(1), StringComparison.Ordinal);
        Assert.Contains(Sentinel, reader.GetString(2), StringComparison.Ordinal);
    }

    private static async Task AssertValidationFailedOnPosterLayoutAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal(ApiErrorCodes.ValidationFailed, doc.RootElement.GetProperty("code").GetString());
        Assert.True(doc.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty("posterLayout", out _));
    }

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<JsonDocument> GetEventAsync(long id)
    {
        var response = await SendAdminAsync(HttpMethod.Get, $"/admin/events/{id}", "");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<long> CreateEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, created_by, updated_at)
values ($1, $2, 1, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<bool> IsLayoutNullInDbAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select poster_layout is null from event where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        return (bool)(await cmd.ExecuteScalarAsync() ?? false);
    }

    private async Task<(long Version, string Key)> ReadSnapshotRowAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select version, s3_key from snapshot where id = 1;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetString(1));
    }

    private async Task<byte[]> ReadSnapshotBytesAsync(string key)
    {
        var obj = await _host!.Store.GetObjectAsync(key);
        Assert.NotNull(obj);
        return obj!.Bytes;
    }

    private async Task RunSqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }
}
