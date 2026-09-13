using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Email;

namespace Wmsfo.Api.IntegrationTests;

// A30 acceptance:
//   - a status change with `notify: false` writes an outbox row that sends
//     nothing when the publisher runs
//   - a status change with `notify: true` fans out and bumps `sent_count`
//   - POST /admin/events/{id}/notify announces the current status again
//   - POST /admin/events/{id}/clone with each copy-flag combination
//   - POST /admin/sponsors/{id}/years/{eventYear}/copy-from/{sourceYear}
//     including 409 year_exists
//   - POST /admin/sponsors/import with created/skipped counts
//   - GET /me/alerts lists only sent rows for the caller's subscriptions
public sealed class A30AlertsAndCloneTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A30Host? _host;

    public A30AlertsAndCloneTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await MigrateAndCleanAsync(_fixture.ConnectionString);
        _host = await A30Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private static async Task MigrateAndCleanAsync(string connectionString)
    {
        var opts = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(opts))
        {
            await db.Database.MigrateAsync();
        }
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from alert_delivery;",
            "delete from outbox;",
            "delete from cookie;",
            "delete from location;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from event;",
            "delete from beacon;",
            "delete from subscriber;",
            "delete from sponsor_year;",
            "delete from sponsor;",
            "delete from cookie_type;",
            "delete from person;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
    }

    // ---------- status change with notify: false writes outbox but sends nothing ----------

    [Fact]
    public async Task Status_change_notify_false_writes_outbox_row_but_no_alert()
    {
        var id = await CreateEventAsync(year: 2080);
        // Add a verified email subscriber; still no alert should be sent.
        var personId = await CreatePersonAsync("silent@wmsfo.test");
        await CreateVerifiedSubscriberAsync(personId, "silent@wmsfo.test");

        // 1 → 2 (planned → scheduled) requires scheduled_at.
        await SetScheduledAtAsync(id, DateTimeOffset.UtcNow.AddDays(1));
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":false}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // outbox row exists.
        var outboxCount = await CountAsync(
            "select count(*) from outbox where topic = 'event.status_changed' and (payload->>'eventId')::bigint = $1;", id);
        Assert.Equal(1L, outboxCount);
        // status_notified_at stays null.
        var stamped = await ReadTimestampAsync("select status_notified_at from event where id = $1;", id);
        Assert.Null(stamped);

        // Run the outbox publisher: the row is marked published without a fan-out.
        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_host.Sender.Sent);
        Assert.Equal(0L, await CountAsync("select count(*) from alert_delivery;"));

        // Run the alert-send chore: nothing to send.
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_host.Sender.Sent);
    }

    // ---------- status change with notify: true fans out and bumps sent_count ----------

    [Fact]
    public async Task Status_change_notify_true_fans_out_and_bumps_sent_count()
    {
        var id = await CreateEventAsync(year: 2081);
        await SetScheduledAtAsync(id, DateTimeOffset.UtcNow.AddDays(1));

        var personId = await CreatePersonAsync("alerter@wmsfo.test");
        var subId = await CreateVerifiedSubscriberAsync(personId, "alerter@wmsfo.test");

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":true,\"message\":\"Come out at 6 tonight!\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // status_notified_at set.
        var stamped = await ReadTimestampAsync("select status_notified_at from event where id = $1;", id);
        Assert.NotNull(stamped);

        // History row records notify + message + outbox link.
        long outboxId;
        long historyId;
        bool notify;
        string? message;
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
select id, outbox_id, notify, message
from event_status_history
where event_id = $1
order by id desc limit 1;", conn);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            historyId = reader.GetInt64(0);
            outboxId = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
            notify = reader.GetBoolean(2);
            message = reader.IsDBNull(3) ? null : reader.GetString(3);
        }
        Assert.True(notify);
        Assert.Equal("Come out at 6 tonight!", message);
        Assert.True(outboxId > 0);

        // outbox payload carries historyId.
        var payload = await ReadJsonbAsync("select payload::text from outbox where id = $1;", outboxId);
        using (var d = JsonDocument.Parse(payload!))
        {
            Assert.Equal(historyId, d.RootElement.GetProperty("historyId").GetInt64());
            Assert.Equal("Come out at 6 tonight!", d.RootElement.GetProperty("message").GetString());
        }

        // Publish: fan out one alert_delivery row per subscriber.
        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1L, await CountAsync(
            "select count(*) from alert_delivery where subscriber_id = $1;", subId));

        // Send: bumps sent_count on the history row.
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        var sent = Assert.Single(_host.Sender.Sent);
        Assert.Equal("alerter@wmsfo.test", sent.ToAddress);
        Assert.Equal(EmailTemplates.EventScheduled, sent.TemplateName);
        Assert.Equal("Come out at 6 tonight!", sent.Values["customMessage"]);

        var sentCount = Convert.ToInt32(await ScalarAsync(
            "select sent_count from event_status_history where id = $1;", historyId));
        Assert.Equal(1, sentCount);
    }

    // ---------- POST /admin/events/{id}/notify ----------

    [Fact]
    public async Task Notify_current_status_appends_history_and_sets_status_notified_at()
    {
        var id = await CreateEventAsync(year: 2082);
        var personId = await CreatePersonAsync("re-notify@wmsfo.test");
        var subId = await CreateVerifiedSubscriberAsync(personId, "re-notify@wmsfo.test");

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/notify", "{}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.NotEqual(JsonValueKind.Null, body.RootElement.GetProperty("statusNotifiedAt").ValueKind);

        // history row: from = to = current status, notify = true.
        long historyId; long outboxId; short from; short to;
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
select id, outbox_id, from_status_id, to_status_id
from event_status_history where event_id = $1
order by id desc limit 1;", conn);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            historyId = reader.GetInt64(0);
            outboxId = reader.GetInt64(1);
            from = reader.GetInt16(2);
            to = reader.GetInt16(3);
        }
        Assert.Equal(from, to);
        Assert.True(outboxId > 0);

        // Topic is event.status_notified.
        var topic = (string)await ScalarAsync("select topic from outbox where id = $1;", outboxId);
        Assert.Equal("event.status_notified", topic);

        // Publish + send.
        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1L, await CountAsync(
            "select count(*) from alert_delivery where subscriber_id = $1;", subId));
        Assert.Single(_host.Sender.Sent);

        var sentCount = Convert.ToInt32(await ScalarAsync(
            "select sent_count from event_status_history where id = $1;", historyId));
        Assert.Equal(1, sentCount);
    }

    // ---------- POST /admin/events/{id}/clone ----------

    [Fact]
    public async Task Clone_no_copy_flags_creates_bare_new_event()
    {
        var srcId = await CreateEventAsync(year: 2050);
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{srcId}/clone",
            "{\"year\":2051,\"name\":\"Cloned 2051\"}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal(2051, dto.RootElement.GetProperty("year").GetInt32());
        Assert.Equal(1, dto.RootElement.GetProperty("statusId").GetInt32());
        Assert.False(dto.RootElement.GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task Clone_with_route_and_poster_copies_links()
    {
        var srcId = await CreateEventAsync(year: 2052);
        var routeId = await CreateRouteAsync();
        var posterId = await CreateReadyRasterMediaAsync();
        await LinkEventAsync(srcId, routeId: routeId, posterId: posterId);

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{srcId}/clone",
            $"{{\"year\":2053,\"name\":\"n\",\"copy\":{{\"route\":true,\"poster\":true}}}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadJsonAsync(response);
        Assert.Equal(routeId, dto.RootElement.GetProperty("routeId").GetInt64());
        Assert.Equal(posterId.ToString(), dto.RootElement.GetProperty("routeImageMediaId").GetString());
    }

    [Fact]
    public async Task Clone_with_sponsors_copies_year_rows_including_pinned()
    {
        var srcId = await CreateEventAsync(year: 2060);
        var sponsorA = await CreateSponsorAsync("A");
        var sponsorB = await CreateSponsorAsync("B");
        await UpsertSponsorYearAsync(sponsorA, 2060, pinned: 1);
        await UpsertSponsorYearAsync(sponsorB, 2060, pinned: 2);

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{srcId}/clone",
            "{\"year\":2061,\"name\":\"n\",\"copy\":{\"sponsors\":true}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var count = await CountAsync("select count(*) from sponsor_year where event_year = 2061;");
        Assert.Equal(2L, count);
        var pinnedA = Convert.ToInt32(await ScalarAsync(
            "select pinned_position from sponsor_year where sponsor_id = $1 and event_year = 2061;", sponsorA));
        Assert.Equal(1, pinnedA);
    }

    [Fact]
    public async Task Clone_conflicting_year_is_409_year_taken()
    {
        var srcId = await CreateEventAsync(year: 2070);
        await CreateEventAsync(year: 2071);
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{srcId}/clone",
            "{\"year\":2071,\"name\":\"dup\"}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("year_taken", await ReadCodeAsync(response));
    }

    // ---------- copy-from year ----------

    [Fact]
    public async Task Copy_from_year_creates_new_year_from_existing()
    {
        var sponsorId = await CreateSponsorAsync("Cake Co");
        await UpsertSponsorYearAsync(sponsorId, 2025);
        var response = await SendAdminAsync(HttpMethod.Post,
            $"/admin/sponsors/{sponsorId}/years/2026/copy-from/2025", "");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var exists = await ScalarAsync(
            "select 1 from sponsor_year where sponsor_id = $1 and event_year = 2026;", sponsorId);
        Assert.NotNull(exists);
    }

    [Fact]
    public async Task Copy_from_year_when_target_exists_is_409_year_exists()
    {
        var sponsorId = await CreateSponsorAsync("Bagel Bros");
        await UpsertSponsorYearAsync(sponsorId, 2025);
        await UpsertSponsorYearAsync(sponsorId, 2026);
        var response = await SendAdminAsync(HttpMethod.Post,
            $"/admin/sponsors/{sponsorId}/years/2026/copy-from/2025", "");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("year_exists", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Copy_from_year_missing_source_is_404()
    {
        var sponsorId = await CreateSponsorAsync("Muffin Ltd");
        var response = await SendAdminAsync(HttpMethod.Post,
            $"/admin/sponsors/{sponsorId}/years/2026/copy-from/2025", "");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- sponsor import ----------

    [Fact]
    public async Task Import_reports_created_and_skipped_counts()
    {
        var a = await CreateSponsorAsync("A imp");
        var b = await CreateSponsorAsync("B imp");
        await UpsertSponsorYearAsync(a, 2025);
        await UpsertSponsorYearAsync(b, 2025);
        // b already has 2026 → will be skipped.
        await UpsertSponsorYearAsync(b, 2026);

        var response = await SendAdminAsync(HttpMethod.Post, "/admin/sponsors/import",
            $"{{\"fromYear\":2025,\"toYear\":2026,\"sponsorIds\":[{a},{b}]}}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await ReadJsonAsync(response);
        Assert.Equal(1, doc.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task Import_when_sponsor_has_no_fromYear_row_is_400()
    {
        var a = await CreateSponsorAsync("A no-from");
        var response = await SendAdminAsync(HttpMethod.Post, "/admin/sponsors/import",
            $"{{\"fromYear\":2025,\"toYear\":2026,\"sponsorIds\":[{a}]}}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- GET /me/alerts ----------

    [Fact]
    public async Task My_alerts_lists_only_sent_rows_for_callers_subscriptions()
    {
        var id = await CreateEventAsync(year: 2090);
        await SetScheduledAtAsync(id, DateTimeOffset.UtcNow.AddDays(1));

        // Two people, each with a verified subscription. Only the caller's
        // alert should appear in their /me/alerts response.
        var callerPersonId = await UpsertDevPersonAsync();
        var callerSubId = await CreateVerifiedSubscriberAsync(callerPersonId, "person@wmsfo.test");
        var otherPersonId = await CreatePersonAsync("other@wmsfo.test");
        _ = await CreateVerifiedSubscriberAsync(otherPersonId, "other@wmsfo.test");

        // Trigger a notified status change to fan out to both.
        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":true}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, _host.Sender.Sent.Count);

        // Person calls /me/alerts.
        using var req = _host.PersonRequest(HttpMethod.Get, "/me/alerts");
        var meResponse = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var doc = await ReadJsonAsync(meResponse);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        var item = items[0];
        Assert.Equal(callerSubId, item.GetProperty("subscriptionId").GetInt64());
        Assert.Equal("person@wmsfo.test", item.GetProperty("address").GetString());
        Assert.Equal("event_status", item.GetProperty("kind").GetString());
        Assert.Equal(2, item.GetProperty("statusId").GetInt32());
        Assert.Equal("Santa's flight is scheduled", item.GetProperty("subject").GetString());
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<long> CreateEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, 1, true, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        // Ensure only one is_current at a time.
        await using (var clr = new NpgsqlCommand("update event set is_current = false where is_current;", conn))
            await clr.ExecuteNonQueryAsync();
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task SetScheduledAtAsync(long id, DateTimeOffset scheduled)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "update event set scheduled_at = $1 where id = $2;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = scheduled });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CreateRouteAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into route (name, s3_key, url, sha256, point_count, uploaded_by)
values ('r', 'routes/x-' || floor(random() * 1000000)::text || '.json',
        'https://cdn.example/routes/x.json',
        repeat('0', 64), 2, 'seed')
returning id;", conn);
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<Guid> CreateReadyRasterMediaAsync()
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into media_asset (id, filename, content_type, kind, state, s3_key, size_bytes, width, height,
                         sha256, variants, alt, title, uploaded_by, confirmed_at)
values ($1, 'poster.jpg', 'image/jpeg', 'raster', 'ready',
        'media/' || $1::text || '/poster.jpg', 100, 100, 100,
        repeat('a', 64), '{}'::jsonb, '', '', 'seed', now());", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task LinkEventAsync(long id, long? routeId, Guid? posterId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "update event set route_id = $1, route_image_media_id = $2 where id = $3;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)routeId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)posterId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CreateSponsorAsync(string name)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "insert into sponsor (name, updated_at) values ($1, now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task UpsertSponsorYearAsync(long sponsorId, int year, int? pinned = null)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into sponsor_year (sponsor_id, event_year, amount_donated, active, can_advertise, anonymous, pinned_position)
values ($1, $2, 100.00, true, true, false, $3)
on conflict (sponsor_id, event_year) do update set pinned_position = excluded.pinned_position;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = sponsorId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)pinned ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CreatePersonAsync(string email)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "insert into person (cognito_sub, email) values (gen_random_uuid(), $1) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    // Ensures the DevStaticTokens person exists in the database with a matching
    // cognito_sub so DbPersonUpsert re-uses it. Called before /me tests so the
    // subscription foreign key resolves to a known id.
    private async Task<long> UpsertDevPersonAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into person (cognito_sub, email) values ($1::uuid, $2)
on conflict (cognito_sub) do update set email = excluded.email
returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(DevStaticTokens.PersonSub) });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = DevStaticTokens.PersonEmail });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> CreateVerifiedSubscriberAsync(long personId, string address)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at)
values ($1, 'email', $2, $3, now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "wsu_" + Guid.NewGuid().ToString("N") });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> CountAsync(string sql, long? param = null)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (param is not null)
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = param.Value });
        var r = await cmd.ExecuteScalarAsync();
        return r is null ? 0 : Convert.ToInt64(r);
    }

    private async Task<object> ScalarAsync(string sql, long? param = null)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (param is not null)
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = param.Value });
        var r = await cmd.ExecuteScalarAsync();
        return r ?? DBNull.Value;
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
