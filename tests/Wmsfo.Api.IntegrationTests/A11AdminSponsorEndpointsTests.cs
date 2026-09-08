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

namespace Wmsfo.Api.IntegrationTests;

// A11 sponsors acceptance:
//   - list / get / create / patch / delete / years-upsert / years-delete
//   - PATCH with logoMediaId honours 404 media-not-found + 409 media_not_ready
//   - the [snapshot] frame bumps the snapshot version on every write
//   - the current-event snapshot embeds sponsors in the amount desc / name asc /
//     id asc ordering (contracts 1.3) and hides sponsors that fail the filter
public sealed class A11AdminSponsorEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A11Host? _host;

    public A11AdminSponsorEndpointsTests(PostgresFixture fixture)
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
        foreach (var sql in new[]
        {
            "delete from cookie;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from location;",
            "delete from event;",
            "delete from sponsor_year;",
            "delete from sponsor;",
            "delete from media_asset;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
    }

    // ---------- CRUD ----------

    [Fact]
    public async Task Create_returns_201_and_persists()
    {
        var body = "{\"name\":\"Alpine Bakeries\",\"contactPerson\":\"Sam\",\"email\":\"sam@example.com\",\"phone\":\"555\",\"address\":\"1 Main\",\"websiteUrl\":\"https://alpine.example\"}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/sponsors", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal("Alpine Bakeries", dto.RootElement.GetProperty("name").GetString());
        Assert.Equal("https://alpine.example", dto.RootElement.GetProperty("websiteUrl").GetString());
        Assert.True(dto.RootElement.TryGetProperty("years", out _));
    }

    [Fact]
    public async Task Create_missing_name_is_400_validation_failed()
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/sponsors", "{\"name\":\"\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Create_bad_url_is_400_validation_failed()
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/sponsors",
            "{\"name\":\"x\",\"websiteUrl\":\"not-a-url\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Get_unknown_id_returns_404()
    {
        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/sponsors/999999");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_orders_by_name_asc()
    {
        await CreateSponsorAsync("Bravo");
        await CreateSponsorAsync("Alpha");
        await CreateSponsorAsync("Charlie");
        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/sponsors");
        var response = await _host.Client.SendAsync(req);
        var doc = await ReadJsonAsync(response);
        var names = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, names);
    }

    [Fact]
    public async Task Patch_unknown_id_is_404()
    {
        var response = await SendAdminAsync(HttpMethod.Patch, "/admin/sponsors/999999", "{\"name\":\"x\"}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_logo_media_not_found_is_404()
    {
        var id = await CreateSponsorAsync("Logo Test");
        var missingId = Guid.NewGuid();
        var response = await SendAdminAsync(HttpMethod.Patch, $"/admin/sponsors/{id}",
            "{\"logoMediaId\":\"" + missingId + "\"}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_logo_media_pending_is_409_media_not_ready()
    {
        var id = await CreateSponsorAsync("Pending Logo");
        var mediaId = await InsertMediaAsync(state: "pending", kind: "raster");
        var response = await SendAdminAsync(HttpMethod.Patch, $"/admin/sponsors/{id}",
            "{\"logoMediaId\":\"" + mediaId + "\"}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("media_not_ready", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Patch_logo_media_ready_sets_field_and_bumps_snapshot()
    {
        var id = await CreateSponsorAsync("Ready Logo");
        var mediaId = await InsertMediaAsync(state: "ready", kind: "raster");
        var vBefore = await ReadSnapshotVersionAsync();
        var response = await SendAdminAsync(HttpMethod.Patch, $"/admin/sponsors/{id}",
            "{\"logoMediaId\":\"" + mediaId + "\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal(mediaId.ToString(), dto.RootElement.GetProperty("logoMediaId").GetString());
        Assert.True(dto.RootElement.TryGetProperty("logo", out var logo));
        Assert.Equal("ready", logo.GetProperty("state").GetString());
        var vAfter = await ReadSnapshotVersionAsync();
        Assert.True(vAfter > vBefore);
    }

    [Fact]
    public async Task Delete_ok_204_and_snapshot_bumps()
    {
        var id = await CreateSponsorAsync("Doomed");
        var vBefore = await ReadSnapshotVersionAsync();
        using var req = _host!.AdminRequest(HttpMethod.Delete, $"/admin/sponsors/{id}");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var vAfter = await ReadSnapshotVersionAsync();
        Assert.True(vAfter > vBefore);
    }

    [Fact]
    public async Task Delete_unknown_id_is_404()
    {
        using var req = _host!.AdminRequest(HttpMethod.Delete, "/admin/sponsors/999999");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- Years upsert/delete ----------

    [Fact]
    public async Task Upsert_year_is_idempotent()
    {
        var id = await CreateSponsorAsync("Yearly");
        var body = "{\"amountDonated\":500.00,\"active\":true,\"canAdvertise\":true,\"anonymous\":false}";
        var response1 = await SendAdminAsync(HttpMethod.Put, $"/admin/sponsors/{id}/years/2027", body);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        var response2 = await SendAdminAsync(HttpMethod.Put, $"/admin/sponsors/{id}/years/2027", body);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        var count = await CountAsync(
            "select count(*) from sponsor_year where sponsor_id = $1 and event_year = 2027;", id);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Upsert_year_bad_year_is_400()
    {
        var id = await CreateSponsorAsync("Bad Year");
        var body = "{\"amountDonated\":100,\"active\":true,\"canAdvertise\":true,\"anonymous\":false}";
        var response = await SendAdminAsync(HttpMethod.Put, $"/admin/sponsors/{id}/years/1900", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upsert_year_unknown_sponsor_is_404()
    {
        var body = "{\"amountDonated\":10,\"active\":true,\"canAdvertise\":true,\"anonymous\":false}";
        var response = await SendAdminAsync(HttpMethod.Put, "/admin/sponsors/999999/years/2027", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_year_is_idempotent()
    {
        var id = await CreateSponsorAsync("Delete Year");
        var body = "{\"amountDonated\":10,\"active\":true,\"canAdvertise\":true,\"anonymous\":false}";
        await SendAdminAsync(HttpMethod.Put, $"/admin/sponsors/{id}/years/2027", body);
        using var req1 = _host!.AdminRequest(HttpMethod.Delete, $"/admin/sponsors/{id}/years/2027");
        var response1 = await _host.Client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.NoContent, response1.StatusCode);
        using var req2 = _host!.AdminRequest(HttpMethod.Delete, $"/admin/sponsors/{id}/years/2027");
        var response2 = await _host.Client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.NoContent, response2.StatusCode);
    }

    // ---------- Authorization ----------

    [Fact]
    public async Task Person_token_is_403_forbidden()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/sponsors");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.PersonToken);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Editor_token_is_permitted()
    {
        using var req = _host!.EditorRequest(HttpMethod.Get, "/admin/sponsors");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- Snapshot ordering, filter, lingerMs ----------

    [Fact]
    public async Task Snapshot_sponsors_order_by_amount_desc_then_name_then_id()
    {
        var eventId = await CreateCurrentEventAsync(2027);
        var alpha = await CreateSponsorAsync("Alpha");   // amount 200
        var bravo = await CreateSponsorAsync("Bravo");   // amount 200 → tie broken by name
        var charlie = await CreateSponsorAsync("Charlie"); // amount 500 → first
        var delta = await CreateSponsorAsync("Delta");   // amount null → last

        await UpsertYearAsync(charlie, 2027, 500);
        await UpsertYearAsync(alpha, 2027, 200);
        await UpsertYearAsync(bravo, 2027, 200);
        await UpsertYearAsync(delta, 2027, null);

        var snapshot = await ReadSnapshotAsync();
        var sponsors = snapshot.RootElement.GetProperty("sponsors").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo", "Delta" }, sponsors);

        _ = eventId;
    }

    [Fact]
    public async Task Snapshot_sponsors_filter_excludes_inactive_anonymous_and_hidden_advertising()
    {
        _ = await CreateCurrentEventAsync(2028);
        var include = await CreateSponsorAsync("Include Me");
        var inactive = await CreateSponsorAsync("Inactive");
        var anon = await CreateSponsorAsync("Anon");
        var noAd = await CreateSponsorAsync("No Ad");

        await UpsertYearAsync(include, 2028, 100, active: true, canAdvertise: true, anonymous: false);
        await UpsertYearAsync(inactive, 2028, 100, active: false, canAdvertise: true, anonymous: false);
        await UpsertYearAsync(anon, 2028, 100, active: true, canAdvertise: true, anonymous: true);
        await UpsertYearAsync(noAd, 2028, 100, active: true, canAdvertise: false, anonymous: false);

        var snapshot = await ReadSnapshotAsync();
        var sponsors = snapshot.RootElement.GetProperty("sponsors").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(new[] { "Include Me" }, sponsors);
    }

    [Fact]
    public async Task Snapshot_lingerMs_uses_settings_and_min_floor()
    {
        _ = await CreateCurrentEventAsync(2029);
        // Set the two settings to deterministic values.
        await PutSettingAsync("sponsor_linger_ms_per_dollar", 40);
        await PutSettingAsync("sponsor_linger_min_ms", 2000);

        var big = await CreateSponsorAsync("Big Donor");
        var small = await CreateSponsorAsync("Small Donor");
        var none = await CreateSponsorAsync("Anonymous Donor");
        await UpsertYearAsync(big, 2029, 100);   // 100 * 40 = 4000 => >2000 -> 4000
        await UpsertYearAsync(small, 2029, 10);   // 10 * 40 = 400   => <2000 -> 2000 (min)
        await UpsertYearAsync(none, 2029, null);  // null            => 2000 (min)

        var snapshot = await ReadSnapshotAsync();
        var lingers = snapshot.RootElement.GetProperty("sponsors").EnumerateArray()
            .Select(s => (s.GetProperty("name").GetString()!, s.GetProperty("lingerMs").GetInt32()))
            .ToDictionary(t => t.Item1, t => t.Item2);
        Assert.Equal(4000, lingers["Big Donor"]);
        Assert.Equal(2000, lingers["Small Donor"]);
        Assert.Equal(2000, lingers["Anonymous Donor"]);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<long> CreateSponsorAsync(string name)
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/sponsors",
            "{\"name\":\"" + name + "\"}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        return dto.RootElement.GetProperty("id").GetInt64();
    }

    private async Task UpsertYearAsync(long sponsorId, int year, decimal? amount,
        bool active = true, bool canAdvertise = true, bool anonymous = false)
    {
        var amt = amount is null ? "null" : amount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var body = $"{{\"amountDonated\":{amt},\"active\":{(active ? "true" : "false")},\"canAdvertise\":{(canAdvertise ? "true" : "false")},\"anonymous\":{(anonymous ? "true" : "false")}}}";
        var response = await SendAdminAsync(HttpMethod.Put, $"/admin/sponsors/{sponsorId}/years/{year}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task PutSettingAsync(string key, int value)
    {
        var body = $"{{\"value\":{value}}}";
        var response = await SendAdminAsync(HttpMethod.Put, $"/admin/settings/{key}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> InsertMediaAsync(string state, string kind)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into media_asset (id, filename, content_type, kind, state, s3_key, uploaded_by, alt, title, variants)
values ($1, 'file.svg', 'image/svg+xml', $2, $3, 'media/' || $1::text || '/file.svg', 'test', '', '', '{}'::jsonb);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = kind });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = state });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<long> CreateCurrentEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var clear = new NpgsqlCommand("update event set is_current = false where is_current;", conn))
            await clear.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, 1, true, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> ReadSnapshotVersionAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select version from snapshot where id = 1;", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> CountAsync(string sql, long? param = null)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (param is not null) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = param.Value });
        var r = await cmd.ExecuteScalarAsync();
        return r is null ? 0 : Convert.ToInt64(r);
    }

    // Read the most recently PUT snapshot object out of the RecordingObjectStore.
    private async Task<JsonDocument> ReadSnapshotAsync()
    {
        // Force a rebuild by patching a sponsor (a no-op patch still commits the
        // frame). Simpler: run POST /admin/snapshot/rebuild — but that endpoint
        // is registered elsewhere. Instead, hit a sponsor-year upsert which the
        // caller has already done in each test.
        var version = await ReadSnapshotVersionAsync();
        var url = await ReadSnapshotUrlAsync();
        Assert.False(string.IsNullOrEmpty(url));
        var key = "snapshots/" + url.Substring(url.LastIndexOf('/') + 1);
        var head = await _host!.Store.GetObjectAsync(key);
        Assert.NotNull(head);
        return JsonDocument.Parse(head!.Bytes);
    }

    private async Task<string> ReadSnapshotUrlAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select url from snapshot where id = 1;", conn);
        return (string)(await cmd.ExecuteScalarAsync() ?? "");
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
