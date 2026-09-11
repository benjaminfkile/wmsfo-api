using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Data;

namespace Wmsfo.Api.IntegrationTests;

// A24 acceptance:
//   - PATCH /admin/events/{id} handles routeImageMediaId with the four outcomes:
//     404 (media not found), 409 media_not_ready, 400 (svg/gif), 200 (raster/ready)
//     and null unlinks the field.
//   - PUT /admin/sponsors/{id}/years/{eventYear} accepts pinnedPosition and
//     lingerMsOverride, with 409 pinned_position_taken from the partial unique index.
//   - GET /admin/sponsors/order/{eventYear} returns SponsorOrderRow[] in snapshot
//     order (pinned first, then amount desc, name, id) with inSnapshot from the
//     1.3 filter.
//   - PUT /admin/sponsors/order/{eventYear} rewrites the pinned list atomically:
//     the listed sponsors get pinned_position 1..n; every other row for that year
//     gets null. Empty list unpins all. 400 validation_failed for an unknown id
//     or a duplicate.
public sealed class A24DesignEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A11Host? _sponsorsHost;
    private A9Host? _eventsHost;

    public A24DesignEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAndCleanAsync();
        _sponsorsHost = await A11Host.StartAsync(_fixture.ConnectionString);
        _eventsHost = await A9Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_sponsorsHost is not null) await _sponsorsHost.DisposeAsync();
        if (_eventsHost is not null) await _eventsHost.DisposeAsync();
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

    // ---------- PATCH /admin/events/{id} routeImageMediaId ----------

    [Fact]
    public async Task Patch_route_image_unknown_media_is_404()
    {
        var eventId = await CreateEventAsync(2027);
        var missing = Guid.NewGuid();
        var response = await SendEventsAdminAsync(HttpMethod.Patch, $"/admin/events/{eventId}",
            "{\"routeImageMediaId\":\"" + missing + "\"}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_route_image_pending_media_is_409_media_not_ready()
    {
        var eventId = await CreateEventAsync(2027);
        var mediaId = await InsertMediaAsync(state: "pending", kind: "raster");
        var response = await SendEventsAdminAsync(HttpMethod.Patch, $"/admin/events/{eventId}",
            "{\"routeImageMediaId\":\"" + mediaId + "\"}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("media_not_ready", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Patch_route_image_svg_is_400_validation_failed()
    {
        var eventId = await CreateEventAsync(2027);
        var mediaId = await InsertMediaAsync(state: "ready", kind: "svg");
        var response = await SendEventsAdminAsync(HttpMethod.Patch, $"/admin/events/{eventId}",
            "{\"routeImageMediaId\":\"" + mediaId + "\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_route_image_gif_is_400_validation_failed()
    {
        var eventId = await CreateEventAsync(2027);
        var mediaId = await InsertMediaAsync(state: "ready", kind: "gif");
        var response = await SendEventsAdminAsync(HttpMethod.Patch, $"/admin/events/{eventId}",
            "{\"routeImageMediaId\":\"" + mediaId + "\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_route_image_ready_raster_sets_link_and_returns_asset()
    {
        var eventId = await CreateEventAsync(2027);
        var mediaId = await InsertMediaAsync(state: "ready", kind: "raster");
        var response = await SendEventsAdminAsync(HttpMethod.Patch, $"/admin/events/{eventId}",
            "{\"routeImageMediaId\":\"" + mediaId + "\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal(mediaId.ToString(), dto.RootElement.GetProperty("routeImageMediaId").GetString());
        var routeImage = dto.RootElement.GetProperty("routeImage");
        Assert.Equal("ready", routeImage.GetProperty("state").GetString());
        Assert.Equal("raster", routeImage.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Patch_route_image_empty_string_unlinks()
    {
        var eventId = await CreateEventAsync(2027);
        var mediaId = await InsertMediaAsync(state: "ready", kind: "raster");
        await SendEventsAdminAsync(HttpMethod.Patch, $"/admin/events/{eventId}",
            "{\"routeImageMediaId\":\"" + mediaId + "\"}");

        var unlink = await SendEventsAdminAsync(HttpMethod.Patch, $"/admin/events/{eventId}",
            "{\"routeImageMediaId\":\"\"}");
        Assert.Equal(HttpStatusCode.OK, unlink.StatusCode);
        var dto = await ReadJsonAsync(unlink);
        Assert.Equal(JsonValueKind.Null, dto.RootElement.GetProperty("routeImageMediaId").ValueKind);
        Assert.Equal(JsonValueKind.Null, dto.RootElement.GetProperty("routeImage").ValueKind);
    }

    // ---------- PUT /admin/sponsors/{id}/years/{eventYear} pinned/linger ----------

    [Fact]
    public async Task Upsert_year_accepts_pinnedPosition_and_lingerMsOverride()
    {
        var id = await CreateSponsorAsync("Pinned");
        var body = "{\"amountDonated\":50.00,\"active\":true,\"canAdvertise\":true,\"anonymous\":false,\"pinnedPosition\":3,\"lingerMsOverride\":7000}";
        var response = await SendSponsorsAdminAsync(HttpMethod.Put, $"/admin/sponsors/{id}/years/2027", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        var year = dto.RootElement.GetProperty("years").EnumerateArray().First(y => y.GetProperty("eventYear").GetInt32() == 2027);
        Assert.Equal(3, year.GetProperty("pinnedPosition").GetInt32());
        Assert.Equal(7000, year.GetProperty("lingerMsOverride").GetInt32());
        Assert.Equal(7000, year.GetProperty("lingerMs").GetInt32());
    }

    [Fact]
    public async Task Upsert_year_pinned_position_taken_is_409()
    {
        var first = await CreateSponsorAsync("First");
        var second = await CreateSponsorAsync("Second");
        var body1 = "{\"amountDonated\":10,\"active\":true,\"canAdvertise\":true,\"anonymous\":false,\"pinnedPosition\":1,\"lingerMsOverride\":null}";
        var r1 = await SendSponsorsAdminAsync(HttpMethod.Put, $"/admin/sponsors/{first}/years/2027", body1);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        var body2 = "{\"amountDonated\":10,\"active\":true,\"canAdvertise\":true,\"anonymous\":false,\"pinnedPosition\":1,\"lingerMsOverride\":null}";
        var r2 = await SendSponsorsAdminAsync(HttpMethod.Put, $"/admin/sponsors/{second}/years/2027", body2);
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        Assert.Equal("pinned_position_taken", await ReadCodeAsync(r2));
    }

    [Fact]
    public async Task Upsert_year_rejects_out_of_range_values()
    {
        var id = await CreateSponsorAsync("BadRanges");
        var badPin = "{\"amountDonated\":null,\"active\":true,\"canAdvertise\":true,\"anonymous\":false,\"pinnedPosition\":2000,\"lingerMsOverride\":null}";
        var r1 = await SendSponsorsAdminAsync(HttpMethod.Put, $"/admin/sponsors/{id}/years/2027", badPin);
        Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);

        var badLinger = "{\"amountDonated\":null,\"active\":true,\"canAdvertise\":true,\"anonymous\":false,\"pinnedPosition\":null,\"lingerMsOverride\":700000}";
        var r2 = await SendSponsorsAdminAsync(HttpMethod.Put, $"/admin/sponsors/{id}/years/2027", badLinger);
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
    }

    // ---------- GET/PUT /admin/sponsors/order/{eventYear} ----------

    [Fact]
    public async Task Get_order_returns_snapshot_ordering_and_inSnapshot_flag()
    {
        var alpha = await CreateSponsorAsync("Alpha");
        var bravo = await CreateSponsorAsync("Bravo");
        var charlie = await CreateSponsorAsync("Charlie");
        var hidden = await CreateSponsorAsync("Hidden");

        await UpsertYearAsync(alpha, 2027, 100m, pinnedPosition: 2);
        await UpsertYearAsync(bravo, 2027, 500m);
        await UpsertYearAsync(charlie, 2027, 200m, pinnedPosition: 1);
        // Hidden has a smaller amount so ordering places bravo before it; it
        // is inactive so inSnapshot is false.
        await UpsertYearAsync(hidden, 2027, 50m, active: false);

        var response = await SendSponsorsAdminAsync(HttpMethod.Get, "/admin/sponsors/order/2027", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(4, items.Length);
        // Charlie (pin 1) first, Alpha (pin 2), then unpinned by amount desc.
        Assert.Equal(charlie, items[0].GetProperty("sponsorId").GetInt64());
        Assert.Equal(1, items[0].GetProperty("pinnedPosition").GetInt32());
        Assert.True(items[0].GetProperty("inSnapshot").GetBoolean());
        Assert.Equal(alpha, items[1].GetProperty("sponsorId").GetInt64());
        Assert.Equal(2, items[1].GetProperty("pinnedPosition").GetInt32());
        Assert.Equal(bravo, items[2].GetProperty("sponsorId").GetInt64());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("pinnedPosition").ValueKind);
        Assert.Equal(hidden, items[3].GetProperty("sponsorId").GetInt64());
        Assert.False(items[3].GetProperty("inSnapshot").GetBoolean());
    }

    [Fact]
    public async Task Put_order_pins_the_listed_ids_and_clears_the_rest()
    {
        var alpha = await CreateSponsorAsync("Alpha");
        var bravo = await CreateSponsorAsync("Bravo");
        var charlie = await CreateSponsorAsync("Charlie");
        // Seed some existing pins that must be cleared.
        await UpsertYearAsync(alpha, 2027, 100m, pinnedPosition: 5);
        await UpsertYearAsync(bravo, 2027, 100m, pinnedPosition: 6);
        await UpsertYearAsync(charlie, 2027, 100m);

        var body = "{\"pinnedSponsorIds\":[" + charlie + "," + alpha + "]}";
        var response = await SendSponsorsAdminAsync(HttpMethod.Put, "/admin/sponsors/order/2027", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Charlie gets 1, Alpha gets 2, Bravo cleared.
        Assert.Equal(1, await ReadPinnedPositionAsync(charlie, 2027));
        Assert.Equal(2, await ReadPinnedPositionAsync(alpha, 2027));
        Assert.Null(await ReadPinnedPositionAsync(bravo, 2027));
    }

    [Fact]
    public async Task Put_order_empty_list_unpins_all()
    {
        var alpha = await CreateSponsorAsync("Alpha");
        var bravo = await CreateSponsorAsync("Bravo");
        await UpsertYearAsync(alpha, 2027, 100m, pinnedPosition: 1);
        await UpsertYearAsync(bravo, 2027, 100m, pinnedPosition: 2);

        var body = "{\"pinnedSponsorIds\":[]}";
        var response = await SendSponsorsAdminAsync(HttpMethod.Put, "/admin/sponsors/order/2027", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Null(await ReadPinnedPositionAsync(alpha, 2027));
        Assert.Null(await ReadPinnedPositionAsync(bravo, 2027));
    }

    [Fact]
    public async Task Put_order_unknown_id_is_400_validation_failed()
    {
        var alpha = await CreateSponsorAsync("Alpha");
        await UpsertYearAsync(alpha, 2027, 100m);

        var body = "{\"pinnedSponsorIds\":[" + alpha + ",999999]}";
        var response = await SendSponsorsAdminAsync(HttpMethod.Put, "/admin/sponsors/order/2027", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = await ReadJsonAsync(response);
        Assert.Equal("validation_failed", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_order_duplicate_id_is_400_validation_failed()
    {
        var alpha = await CreateSponsorAsync("Alpha");
        await UpsertYearAsync(alpha, 2027, 100m);

        var body = "{\"pinnedSponsorIds\":[" + alpha + "," + alpha + "]}";
        var response = await SendSponsorsAdminAsync(HttpMethod.Put, "/admin/sponsors/order/2027", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<long> CreateSponsorAsync(string name)
    {
        var response = await SendSponsorsAdminAsync(HttpMethod.Post, "/admin/sponsors",
            "{\"name\":\"" + name + "\"}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        return dto.RootElement.GetProperty("id").GetInt64();
    }

    private async Task UpsertYearAsync(long sponsorId, int year, decimal? amount,
        bool active = true, bool canAdvertise = true, bool anonymous = false,
        int? pinnedPosition = null, int? lingerMsOverride = null)
    {
        var amt = amount is null ? "null" : amount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pin = pinnedPosition is null ? "null" : pinnedPosition.Value.ToString();
        var linger = lingerMsOverride is null ? "null" : lingerMsOverride.Value.ToString();
        var body = "{\"amountDonated\":" + amt +
            ",\"active\":" + (active ? "true" : "false") +
            ",\"canAdvertise\":" + (canAdvertise ? "true" : "false") +
            ",\"anonymous\":" + (anonymous ? "true" : "false") +
            ",\"pinnedPosition\":" + pin +
            ",\"lingerMsOverride\":" + linger + "}";
        var response = await SendSponsorsAdminAsync(HttpMethod.Put, $"/admin/sponsors/{sponsorId}/years/{year}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<long> CreateEventAsync(int year)
    {
        var response = await SendEventsAdminAsync(HttpMethod.Post, "/admin/events",
            $"{{\"year\":{year},\"name\":\"E{year}\",\"scheduledAt\":null,\"fundsPercent\":0,\"routeId\":null,\"inheritRoute\":true}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        return dto.RootElement.GetProperty("id").GetInt64();
    }

    private async Task<int?> ReadPinnedPositionAsync(long sponsorId, int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select pinned_position from sponsor_year where sponsor_id = $1 and event_year = $2;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = sponsorId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        var r = await cmd.ExecuteScalarAsync();
        if (r is null || r is DBNull) return null;
        return Convert.ToInt32(r);
    }

    private async Task<Guid> InsertMediaAsync(string state, string kind)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into media_asset (id, filename, content_type, kind, state, s3_key, uploaded_by, alt, title, variants)
values ($1, 'file', 'application/octet-stream', $2, $3, 'media/' || $1::text || '/file', 'test', '', '', '{}'::jsonb);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = kind });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = state });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<HttpResponseMessage> SendSponsorsAdminAsync(HttpMethod method, string path, string? body)
    {
        using var req = _sponsorsHost!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _sponsorsHost.Client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> SendEventsAdminAsync(HttpMethod method, string path, string? body)
    {
        using var req = _eventsHost!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _eventsHost.Client.SendAsync(req);
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
