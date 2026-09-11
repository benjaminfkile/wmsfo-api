using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// A11 settings acceptance:
//   - GET lists every key from contracts 6, filling defaults for missing rows
//   - PUT validates type (integer) and range per key
//   - PUT is a [snapshot] write; the snapshot version bumps
//   - Unknown key → 404 not_found
public sealed class A11AdminSettingsEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A11Host? _host;

    public A11AdminSettingsEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAndCleanAsync();
        _host = await A11Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task MigrateAndCleanAsync()
    {
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(contextOptions);
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        // Note: leaving app_setting rows in place - the migration seeds them.
        foreach (var sql in new[]
        {
            "delete from event;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
    }

    [Fact]
    public async Task List_returns_all_keys_with_seed_defaults()
    {
        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/settings");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => (e.GetProperty("key").GetString()!, e.GetProperty("value").GetInt32()))
            .ToDictionary(t => t.Item1, t => t.Item2);
        Assert.Equal(5000, items["poll_interval_ms"]);
        Assert.Equal(10, items["cookie_limit_per_person"]);
        Assert.Equal(40, items["sponsor_linger_ms_per_dollar"]);
        Assert.Equal(2000, items["sponsor_linger_min_ms"]);
        Assert.Equal(45, items["beacon_stale_after_s"]);
        Assert.Equal(2000, items["flight_history_max_points"]);
    }

    [Fact]
    public async Task Put_flight_history_max_points_validates_range()
    {
        // flight_history_max_points range: 100..50000
        var lo = await SendAdminAsync(HttpMethod.Put, "/admin/settings/flight_history_max_points",
            "{\"value\":50}");
        Assert.Equal(HttpStatusCode.BadRequest, lo.StatusCode);

        var hi = await SendAdminAsync(HttpMethod.Put, "/admin/settings/flight_history_max_points",
            "{\"value\":99999}");
        Assert.Equal(HttpStatusCode.BadRequest, hi.StatusCode);

        var ok = await SendAdminAsync(HttpMethod.Put, "/admin/settings/flight_history_max_points",
            "{\"value\":1500}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Restore the seed value so tests that check defaults are not order-dependent.
        var restore = await SendAdminAsync(HttpMethod.Put, "/admin/settings/flight_history_max_points",
            "{\"value\":2000}");
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
    }

    [Fact]
    public async Task List_fills_default_for_missing_rows_with_null_updatedBy()
    {
        // Delete the row so the list falls back to the compiled default.
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var del = new NpgsqlCommand(
                "delete from app_setting where key = 'poll_interval_ms';", conn);
            await del.ExecuteNonQueryAsync();
        }

        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/settings");
        var response = await _host.Client.SendAsync(req);
        var doc = await ReadJsonAsync(response);
        var poll = doc.RootElement.GetProperty("items").EnumerateArray()
            .First(e => e.GetProperty("key").GetString() == "poll_interval_ms");
        Assert.Equal(5000, poll.GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, poll.GetProperty("updatedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, poll.GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public async Task Put_unknown_key_is_404()
    {
        var response = await SendAdminAsync(HttpMethod.Put, "/admin/settings/no_such_key", "{\"value\":1}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_bad_type_is_400_validation_failed()
    {
        var response = await SendAdminAsync(HttpMethod.Put, "/admin/settings/poll_interval_ms",
            "{\"value\":\"nope\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Put_below_range_is_400()
    {
        // poll_interval_ms range: 1000..60000
        var response = await SendAdminAsync(HttpMethod.Put, "/admin/settings/poll_interval_ms",
            "{\"value\":500}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_above_range_is_400()
    {
        // beacon_stale_after_s range: 15..3600
        var response = await SendAdminAsync(HttpMethod.Put, "/admin/settings/beacon_stale_after_s",
            "{\"value\":99999}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_valid_updates_row_bumps_snapshot_and_stamps_email()
    {
        var vBefore = await ReadSnapshotVersionAsync();
        var response = await SendAdminAsync(HttpMethod.Put, "/admin/settings/poll_interval_ms",
            "{\"value\":8000}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal("poll_interval_ms", dto.RootElement.GetProperty("key").GetString());
        Assert.Equal(8000, dto.RootElement.GetProperty("value").GetInt32());
        Assert.Equal("admin@wmsfo.test", dto.RootElement.GetProperty("updatedBy").GetString());
        var vAfter = await ReadSnapshotVersionAsync();
        Assert.True(vAfter > vBefore);
    }

    // ---------- Authorization ----------

    [Fact]
    public async Task Editor_token_cannot_write_settings()
    {
        using var req = _host!.EditorRequest(HttpMethod.Put, "/admin/settings/poll_interval_ms");
        req.Content = new StringContent("{\"value\":5000}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Missing_token_is_401()
    {
        var response = await _host!.Client.GetAsync("/admin/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Person_token_is_403_forbidden()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/settings");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.PersonToken);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<long> ReadSnapshotVersionAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select version from snapshot where id = 1;", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
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
