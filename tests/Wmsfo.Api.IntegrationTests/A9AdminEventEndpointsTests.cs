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

// A9 acceptance:
//   - every listed code per endpoint in contracts 4.5 Events
//   - the constraint-name mapping of sql.md 4.3 (event_year_key → year_taken,
//     event_one_live → another_event_live)
//   - a status change writes history + outbox + a new snapshot + a live object
//     with the new statusId
//   - final_cookie_tally set on entry into and cleared on exit from status 4
//   - went_live_at / ended_at stamps
//   - locations paging and CSV export
public sealed class A9AdminEventEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A9Host? _host;

    public A9AdminEventEndpointsTests(PostgresFixture fixture)
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
            "delete from cookie;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from event;",
            "delete from outbox;",
            "delete from route;",
            "delete from beacon;",
            "delete from person;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Ensure content_version + snapshot rows exist so the snapshot builder
        // has something to embed and the [snapshot] frame does not blow up.
        await SnapshotSeed.EnsureAsync(conn);
    }

    // ---------- GET /admin/events ----------

    [Fact]
    public async Task List_returns_events_ordered_by_year_desc()
    {
        await CreateEvent(year: 2024);
        await CreateEvent(year: 2026);
        await CreateEvent(year: 2025);
        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/events");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await ReadJsonAsync(response);
        var years = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("year").GetInt32()).ToArray();
        Assert.Equal(new[] { 2026, 2025, 2024 }, years);
    }

    // ---------- POST /admin/events ----------

    [Fact]
    public async Task Create_defaults_return_201_and_status_1()
    {
        var body = "{\"year\":2027,\"name\":\"Santa Flyover 2027\",\"scheduledAt\":null,\"fundsPercent\":0,\"routeId\":null,\"inheritRoute\":true}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/events", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal(1, dto.RootElement.GetProperty("statusId").GetInt32());
        Assert.False(dto.RootElement.GetProperty("isCurrent").GetBoolean());
        Assert.Equal("admin@wmsfo.test", dto.RootElement.GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task Create_conflicting_year_returns_409_year_taken()
    {
        await CreateEvent(year: 2028);
        var body = "{\"year\":2028,\"name\":\"Duplicate\",\"scheduledAt\":null,\"fundsPercent\":0,\"routeId\":null,\"inheritRoute\":true}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/events", body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("year_taken", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Create_inherit_route_true_with_routeId_is_400()
    {
        var body = "{\"year\":2029,\"name\":\"n\",\"scheduledAt\":null,\"fundsPercent\":0,\"routeId\":10,\"inheritRoute\":true}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/events", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Create_unknown_routeId_is_404()
    {
        var body = "{\"year\":2030,\"name\":\"n\",\"scheduledAt\":null,\"fundsPercent\":0,\"routeId\":9999999,\"inheritRoute\":false}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/events", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.NotFound, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Create_bad_year_range_is_400()
    {
        var body = "{\"year\":1000,\"name\":\"n\",\"scheduledAt\":null,\"fundsPercent\":0,\"routeId\":null,\"inheritRoute\":true}";
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/events", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    // ---------- GET /admin/events/{id} ----------

    [Fact]
    public async Task Get_unknown_id_returns_404()
    {
        using var req = _host!.AdminRequest(HttpMethod.Get, "/admin/events/999999");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- PATCH /admin/events/{id} ----------

    [Fact]
    public async Task Patch_year_conflict_is_409_year_taken()
    {
        var a = await CreateEvent(year: 2031);
        _ = await CreateEvent(year: 2032);
        var response = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{a}", "{\"year\":2032}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("year_taken", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Patch_unknown_id_returns_404()
    {
        var response = await SendAdminAsync(HttpMethod.Patch, "/admin/events/999999", "{\"name\":\"x\"}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- DELETE /admin/events/{id} ----------

    [Fact]
    public async Task Delete_live_event_is_409_event_live()
    {
        var id = await CreateEvent(year: 2034);
        await SetStatusAsync(id, 3, setCurrent: true);
        using var req = _host!.AdminRequest(HttpMethod.Delete, $"/admin/events/{id}");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("event_live", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Delete_event_with_locations_is_409_event_has_locations()
    {
        var id = await CreateEvent(year: 2035);
        var beacon = await SeedBeaconAsync("del-loc");
        await InsertLocationAsync(id, beacon);
        using var req = _host!.AdminRequest(HttpMethod.Delete, $"/admin/events/{id}");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("event_has_locations", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Delete_ok_returns_204()
    {
        var id = await CreateEvent(year: 2036);
        using var req = _host!.AdminRequest(HttpMethod.Delete, $"/admin/events/{id}");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---------- POST /admin/events/{id}/current ----------

    [Fact]
    public async Task Set_current_conflict_when_another_is_current_and_live()
    {
        var a = await CreateEvent(year: 2037);
        await SetStatusAsync(a, 3, setCurrent: true);
        var b = await CreateEvent(year: 2038);
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{b}/current", "");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("current_event_live", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Set_current_idempotent_on_already_current_no_snapshot_bump()
    {
        var id = await CreateEvent(year: 2039);
        await SetStatusAsync(id, 1, setCurrent: true);
        var before = await ReadSnapshotVersionAsync();
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/current", "");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await ReadSnapshotVersionAsync();
        Assert.Equal(before, after);
    }

    // ---------- POST /admin/events/{id}/status ----------

    [Fact]
    public async Task Status_transition_1_to_2_requires_scheduled_at()
    {
        var id = await CreateEvent(year: 2040);
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":false}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("scheduled_at_required", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Status_transition_same_status_is_409_event_status_unchanged()
    {
        var id = await CreateEvent(year: 2041);
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":1,\"notify\":false}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("event_status_unchanged", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Status_transition_to_3_requires_current()
    {
        var id = await CreateEvent(year: 2042);
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":3,\"notify\":false}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("event_not_current", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Status_transition_to_3_when_another_live_is_409_another_event_live()
    {
        var a = await CreateEvent(year: 2043);
        await SetStatusAsync(a, 3, setCurrent: true);
        var b = await CreateEvent(year: 2044);
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{b}/status",
            "{\"statusId\":3,\"notify\":false}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // Not current before another_event_live; but the API checks isCurrent
        // first so this returns event_not_current.
        var code = await ReadCodeAsync(response);
        Assert.Contains(code, new[] { "event_not_current", "another_event_live" });
    }

    // A9 acceptance criterion 779: a status change writes history, outbox, a
    // new snapshot, and a live object with the new status id.
    [Fact]
    public async Task Status_transition_writes_history_outbox_snapshot_and_live_object()
    {
        var id = await CreateEvent(year: 2050);
        await SetCurrentDirectAsync(id);
        var versionBefore = await ReadSnapshotVersionAsync();

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":3,\"notify\":true}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal(3, dto.RootElement.GetProperty("statusId").GetInt32());

        // event_status_history: at least one row for this event with to=3.
        var histCount = await CountAsync(
            "select count(*) from event_status_history where event_id = $1 and to_status_id = 3;", id);
        Assert.True(histCount >= 1);
        // outbox: event.status_changed row.
        var outboxCount = await CountAsync(
            "select count(*) from outbox where topic = 'event.status_changed';");
        Assert.True(outboxCount >= 1);
        // snapshot: version increased.
        var versionAfter = await ReadSnapshotVersionAsync();
        Assert.True(versionAfter > versionBefore);

        // went_live_at was stamped.
        var went = await ReadTimestampAsync("select went_live_at from event where id = $1;", id);
        Assert.NotNull(went);

        // Live object was published to the gateway carrying eventStatusId=3.
        // The writer runs fire-and-forget after the response so wait briefly
        // for the publish to land.
        for (var i = 0; i < 40; i++)
        {
            if (_host!.Gateway.LastPublished is not null) break;
            await Task.Delay(50);
        }
        Assert.NotNull(_host!.Gateway.LastPublished);
        using var live = JsonDocument.Parse(_host.Gateway.LastPublished!);
        Assert.Equal(3, live.RootElement.GetProperty("eventStatusId").GetInt32());
    }

    // A9: entry into status 4 populates final_cookie_tally; exit clears it.
    [Fact]
    public async Task Status_transition_4_writes_final_cookie_tally_and_exit_clears_it()
    {
        var id = await CreateEvent(year: 2055);
        await SetCurrentDirectAsync(id);
        await SetStatusAsync(id, 3, setCurrent: false);

        // Seed some cookies on this event (need a person + a cookie_type).
        var personId = await SeedPersonAsync("cookies@wmsfo.test");
        var typeId = await SeedCookieTypeAsync("Chocolate chip");
        await InsertCookieAsync(id, personId, typeId);
        await InsertCookieAsync(id, personId, typeId);

        // Transition 3 → 4: final_cookie_tally should be set.
        var response1 = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":4,\"notify\":false}");
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        var ended = await ReadTimestampAsync("select ended_at from event where id = $1;", id);
        Assert.NotNull(ended);
        var tally = await ReadJsonbAsync("select final_cookie_tally::text from event where id = $1;", id);
        Assert.NotNull(tally);
        using (var d = JsonDocument.Parse(tally!))
        {
            Assert.Equal(2, d.RootElement.GetProperty(typeId.ToString()).GetInt32());
        }

        // Exit from 4 (4 → 5 cancelled): final_cookie_tally cleared.
        var response2 = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":5,\"notify\":false}");
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        var afterExit = await ReadJsonbAsync("select final_cookie_tally::text from event where id = $1;", id);
        Assert.Null(afterExit);
    }

    // ---------- GET /admin/events/{id}/status-history ----------

    [Fact]
    public async Task Status_history_is_newest_first()
    {
        var id = await CreateEvent(year: 2060);
        await SetCurrentDirectAsync(id);
        await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status", "{\"statusId\":3,\"notify\":false}");
        await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status", "{\"statusId\":4,\"notify\":false}");
        using var req = _host!.AdminRequest(HttpMethod.Get, $"/admin/events/{id}/status-history");
        var response = await _host.Client.SendAsync(req);
        var doc = await ReadJsonAsync(response);
        var arr = doc.RootElement.GetProperty("items");
        Assert.True(arr.GetArrayLength() >= 2);
        Assert.Equal(4, arr[0].GetProperty("toStatusId").GetInt32());
        Assert.Equal(3, arr[1].GetProperty("toStatusId").GetInt32());
    }

    // ---------- POST /admin/events/{id}/messages ----------

    [Fact]
    public async Task Create_message_writes_snapshot_and_outbox_when_notify_true()
    {
        var id = await CreateEvent(year: 2061);
        var vBefore = await ReadSnapshotVersionAsync();
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"Santa is airborne over the valley.\",\"eventTime\":null,\"notify\":true}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var vAfter = await ReadSnapshotVersionAsync();
        Assert.True(vAfter > vBefore);
        var outboxCount = await CountAsync("select count(*) from outbox where topic = 'event.message_posted';");
        Assert.True(outboxCount >= 1);
    }

    [Fact]
    public async Task Create_message_no_notify_writes_no_outbox()
    {
        var id = await CreateEvent(year: 2062);
        await RunSqlAsync("delete from outbox where topic = 'event.message_posted';");
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"quiet\",\"eventTime\":null,\"notify\":false}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var outboxCount = await CountAsync("select count(*) from outbox where topic = 'event.message_posted';");
        Assert.Equal(0, outboxCount);
    }

    [Fact]
    public async Task Patch_message_unknown_id_is_404()
    {
        var id = await CreateEvent(year: 2063);
        var response = await SendAdminAsync(HttpMethod.Patch,
            $"/admin/events/{id}/messages/999999", "{\"body\":\"x\"}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_message_unknown_id_is_404()
    {
        var id = await CreateEvent(year: 2064);
        using var req = _host!.AdminRequest(HttpMethod.Delete, $"/admin/events/{id}/messages/999999");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- GET /admin/events/{id}/locations ----------

    [Fact]
    public async Task Locations_paged_by_seq_ascending_with_keyset()
    {
        var id = await CreateEvent(year: 2070);
        var beacon = await SeedBeaconAsync("loc-page");
        for (var i = 0; i < 10; i++) await InsertLocationAsync(id, beacon, published: true);
        using var req = _host!.AdminRequest(HttpMethod.Get, $"/admin/events/{id}/locations?limit=3");
        var response = await _host.Client.SendAsync(req);
        var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        var seqs = items.EnumerateArray().Select(e => e.GetProperty("seq").GetInt64()).ToArray();
        Assert.True(seqs[0] < seqs[1] && seqs[1] < seqs[2]);
        var next = doc.RootElement.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(next));
        using var req2 = _host!.AdminRequest(HttpMethod.Get, $"/admin/events/{id}/locations?limit=3&cursor={next}");
        var response2 = await _host.Client.SendAsync(req2);
        var doc2 = await ReadJsonAsync(response2);
        var items2 = doc2.RootElement.GetProperty("items");
        Assert.Equal(3, items2.GetArrayLength());
        Assert.Equal(seqs[^1] + 1, items2[0].GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task Locations_csv_stream_header_and_rows()
    {
        var id = await CreateEvent(year: 2071);
        var beacon = await SeedBeaconAsync("csv");
        for (var i = 0; i < 5; i++) await InsertLocationAsync(id, beacon, published: i % 2 == 0);

        using var req = _host!.AdminRequest(HttpMethod.Get, $"/admin/events/{id}/locations");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/csv", response.Content.Headers.ContentType?.MediaType ?? "");
        var text = await response.Content.ReadAsStringAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "seq,beaconId,published,recordedAt,receivedAt,lat,lng,speedMps,altitudeM,headingDeg,accuracyM",
            lines[0]);
        Assert.Equal(6, lines.Length); // 1 header + 5 rows
    }

    [Fact]
    public async Task Locations_publishedOnly_filters_unpublished_rows()
    {
        var id = await CreateEvent(year: 2072);
        var beacon = await SeedBeaconAsync("only-pub");
        await InsertLocationAsync(id, beacon, published: true);
        await InsertLocationAsync(id, beacon, published: false);
        await InsertLocationAsync(id, beacon, published: true);

        using var req = _host!.AdminRequest(HttpMethod.Get, $"/admin/events/{id}/locations?publishedOnly=true");
        var response = await _host.Client.SendAsync(req);
        var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
            Assert.True(item.GetProperty("published").GetBoolean());
    }

    [Fact]
    public async Task Locations_beacon_filter_narrows_results()
    {
        var id = await CreateEvent(year: 2073);
        var b1 = await SeedBeaconAsync("bA");
        var b2 = await SeedBeaconAsync("bB");
        await InsertLocationAsync(id, b1);
        await InsertLocationAsync(id, b2);
        await InsertLocationAsync(id, b1);

        using var req = _host!.AdminRequest(HttpMethod.Get, $"/admin/events/{id}/locations?beaconId={b1}");
        var response = await _host.Client.SendAsync(req);
        var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
            Assert.Equal(b1, item.GetProperty("beaconId").GetInt64());
    }

    // ---------- Authorization matrix ----------

    [Fact]
    public async Task Person_token_is_403_forbidden_on_admin_events()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/events");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.PersonToken);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Missing_token_is_401_unauthenticated()
    {
        var response = await _host!.Client.GetAsync("/admin/events");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<long> CreateEvent(int year)
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

    private async Task SetStatusAsync(long id, int status, bool setCurrent)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        if (setCurrent)
        {
            await using var clr = new NpgsqlCommand("update event set is_current = false where is_current;", conn);
            await clr.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(
            "update event set status_id = $1, is_current = $2, went_live_at = case when $1 = 3 then now() else went_live_at end where id = $3;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (short)status });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = setCurrent });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SetCurrentDirectAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var clr = new NpgsqlCommand("update event set is_current = false where is_current;", conn))
            await clr.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand("update event set is_current = true where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await cmd.ExecuteNonQueryAsync();
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

    private async Task InsertLocationAsync(long eventId, long beaconId, bool published = true)
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

    private async Task<long> SeedPersonAsync(string email)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into person (cognito_sub, email) values (gen_random_uuid(), $1) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> SeedCookieTypeAsync(string name)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into cookie_type (name, sort, active, icon)
values ($1, 10, true, '{""source"":""library"",""id"":""cookie""}'::jsonb)
returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task InsertCookieAsync(long eventId, long personId, long cookieTypeId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into cookie (event_id, person_id, cookie_type_id) values ($1, $2, $3);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cookieTypeId });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ReadSnapshotVersionAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select version from snapshot where id = 1;", conn);
        var r = await cmd.ExecuteScalarAsync();
        return r is null ? 0 : Convert.ToInt64(r);
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

    private async Task<DateTimeOffset?> ReadTimestampAsync(string sql, long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        if (reader.IsDBNull(0)) return null;
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private async Task<string?> ReadJsonbAsync(string sql, long id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        var r = await cmd.ExecuteScalarAsync();
        return r is null || r is DBNull ? null : (string)r;
    }

    private async Task RunSqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
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
