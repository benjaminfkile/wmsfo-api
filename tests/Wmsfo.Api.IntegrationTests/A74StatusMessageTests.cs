using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Email;

namespace Wmsfo.Api.IntegrationTests;

// A74 acceptance: the text typed on a status change or an announcement is an
// event_message row.
//   - a status change with notify true and a message creates the row, the
//     history row and its DTO reference it, the snapshot's latestMessage is
//     that message, the outbox payload carries messageId, and the email
//     carries the text and not the stock paragraph; the audit `after`
//     carries messageId
//   - notify false with a message creates the row and sends nothing
//   - no message posts the stock paragraph as the row, and the email carries
//     it once
//   - an announce with a message does the same as a change
//   - deleting the message before the send renders the stock paragraph and
//     leaves the history row with null message and messageId
//   - GET /me/alerts reports messageId on the status alert
public sealed class A74StatusMessageTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Text = "Come out at 6 tonight!";
    private static readonly DateTimeOffset Scheduled = DateTimeOffset.Parse("2034-12-24T23:00:00Z");

    private readonly PostgresFixture _fixture;
    private A30Host? _host;
    private EmailTemplates? _templates;

    public A74StatusMessageTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        var opts = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(opts))
            await db.Database.MigrateAsync();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
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
            "delete from subscriber;",
            "delete from person;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
        _host = await A30Host.StartAsync(_fixture.ConnectionString);
        _templates = EmailTemplates.Load(TestPaths.EmailTemplatesDir);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Status_change_with_notify_and_message_posts_an_event_message_the_email_carries()
    {
        var id = await CreateEventAsync(2101);
        var subscriberAddress = "change@wmsfo.test";
        await CreateVerifiedSubscriberAsync(await CreatePersonAsync(subscriberAddress), subscriberAddress);

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":true,\"message\":\"  " + Text + "  \"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The event_message row: trimmed body, no event time, created by the actor.
        var message = Assert.Single(await ReadMessagesAsync(id));
        Assert.Equal(Text, message.Body);
        Assert.Null(message.EventTime);
        Assert.Equal(DevStaticTokens.AdminEmail, message.CreatedBy);

        // The history row references it, and the DTO carries the body and id.
        var history = await ReadNewestHistoryAsync(id);
        Assert.Equal(message.Id, history.GetProperty("messageId").GetInt64());
        Assert.Equal(Text, history.GetProperty("message").GetString());
        Assert.Equal(message.Id, Convert.ToInt64(await ScalarAsync(
            "select message_id from event_status_history where id = $1;", history.GetProperty("id").GetInt64())));

        // The snapshot's latestMessage is that message.
        using (var snapshot = await ReadSnapshotAsync())
        {
            var latest = snapshot.RootElement.GetProperty("event").GetProperty("latestMessage");
            Assert.Equal(message.Id, latest.GetProperty("id").GetInt64());
            Assert.Equal(Text, latest.GetProperty("body").GetString());
        }

        // The outbox payload carries messageId and not the text.
        using (var payload = await ReadPayloadAsync(id, "event.status_changed"))
        {
            Assert.Equal(message.Id, payload.RootElement.GetProperty("messageId").GetInt64());
            Assert.False(payload.RootElement.TryGetProperty("message", out _));
        }

        // The audit row's after carries messageId.
        using (var after = await ReadAuditAfterAsync(id, "status"))
            Assert.Equal(message.Id, after.RootElement.GetProperty("messageId").GetInt64());

        // The one email carries the text and not the stock paragraph.
        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        var sent = Assert.Single(_host.Sender.Sent);
        Assert.Equal(EmailTemplates.EventScheduled, sent.TemplateName);
        var rendered = _templates!.Render(sent.TemplateName, sent.Values);
        var stock = EmailTemplates.StockParagraph(2, "Event 2101", Scheduled);
        Assert.Contains(Text, rendered.Text);
        Assert.Contains(Text, rendered.Html);
        Assert.DoesNotContain(stock, rendered.Text);
    }

    [Fact]
    public async Task Status_change_with_notify_false_and_message_posts_the_row_and_sends_nothing()
    {
        var id = await CreateEventAsync(2102);
        var subscriberAddress = "quiet@wmsfo.test";
        await CreateVerifiedSubscriberAsync(await CreatePersonAsync(subscriberAddress), subscriberAddress);

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":false,\"message\":\"" + Text + "\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var message = Assert.Single(await ReadMessagesAsync(id));
        Assert.Equal(Text, message.Body);
        var history = await ReadNewestHistoryAsync(id);
        Assert.False(history.GetProperty("notify").GetBoolean());
        Assert.Equal(message.Id, history.GetProperty("messageId").GetInt64());
        using (var payload = await ReadPayloadAsync(id, "event.status_changed"))
            Assert.Equal(message.Id, payload.RootElement.GetProperty("messageId").GetInt64());

        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_host.Sender.Sent);
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync("select count(*) from alert_delivery;")));
    }

    [Fact]
    public async Task Status_change_without_message_posts_the_stock_paragraph_the_email_carries_once()
    {
        var id = await CreateEventAsync(2103);
        var subscriberAddress = "stock@wmsfo.test";
        await CreateVerifiedSubscriberAsync(await CreatePersonAsync(subscriberAddress), subscriberAddress);

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":true}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stock = EmailTemplates.StockParagraph(2, "Event 2103", Scheduled);
        var message = Assert.Single(await ReadMessagesAsync(id));
        Assert.Equal(stock, message.Body);
        Assert.Null(message.EventTime);
        Assert.Equal(DevStaticTokens.AdminEmail, message.CreatedBy);
        var history = await ReadNewestHistoryAsync(id);
        Assert.Equal(stock, history.GetProperty("message").GetString());
        Assert.Equal(message.Id, history.GetProperty("messageId").GetInt64());
        using (var payload = await ReadPayloadAsync(id, "event.status_changed"))
            Assert.Equal(message.Id, payload.RootElement.GetProperty("messageId").GetInt64());
        using (var after = await ReadAuditAfterAsync(id, "status"))
            Assert.Equal(message.Id, after.RootElement.GetProperty("messageId").GetInt64());

        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        var sent = Assert.Single(_host.Sender.Sent);
        Assert.Equal(stock, sent.Values["customMessage"]);
        var rendered = _templates!.Render(sent.TemplateName, sent.Values);
        Assert.Equal(2, rendered.Text.Split(stock).Length);
        Assert.Equal(2, rendered.Html.Split(stock).Length);
    }

    [Fact]
    public async Task Announce_with_message_posts_an_event_message_the_email_carries()
    {
        var id = await CreateEventAsync(2104);
        var subscriberAddress = "announce@wmsfo.test";
        await CreateVerifiedSubscriberAsync(await CreatePersonAsync(subscriberAddress), subscriberAddress);
        var versionBefore = await ReadSnapshotVersionAsync();

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/notify",
            "{\"message\":\"" + Text + "\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var message = Assert.Single(await ReadMessagesAsync(id));
        Assert.Equal(Text, message.Body);
        Assert.Null(message.EventTime);
        Assert.Equal(DevStaticTokens.AdminEmail, message.CreatedBy);

        var history = await ReadNewestHistoryAsync(id);
        Assert.Equal(history.GetProperty("fromStatusId").GetInt32(), history.GetProperty("toStatusId").GetInt32());
        Assert.Equal(message.Id, history.GetProperty("messageId").GetInt64());
        Assert.Equal(Text, history.GetProperty("message").GetString());

        // The announce with a message rebuilds the snapshot.
        Assert.True(await ReadSnapshotVersionAsync() > versionBefore);
        using (var snapshot = await ReadSnapshotAsync())
        {
            var latest = snapshot.RootElement.GetProperty("event").GetProperty("latestMessage");
            Assert.Equal(message.Id, latest.GetProperty("id").GetInt64());
        }

        using (var payload = await ReadPayloadAsync(id, "event.status_notified"))
        {
            Assert.Equal(message.Id, payload.RootElement.GetProperty("messageId").GetInt64());
            Assert.False(payload.RootElement.TryGetProperty("message", out _));
        }
        using (var after = await ReadAuditAfterAsync(id, "notify"))
            Assert.Equal(message.Id, after.RootElement.GetProperty("messageId").GetInt64());

        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        var sent = Assert.Single(_host.Sender.Sent);
        Assert.Equal(EmailTemplates.EventPlanned, sent.TemplateName);
        var rendered = _templates!.Render(sent.TemplateName, sent.Values);
        Assert.Contains(Text, rendered.Text);
        Assert.DoesNotContain(EmailTemplates.StockParagraph(1, "Event 2104", Scheduled), rendered.Text);
    }

    [Fact]
    public async Task Announce_without_message_posts_no_row_and_leaves_the_snapshot()
    {
        var id = await CreateEventAsync(2105);
        var versionBefore = await ReadSnapshotVersionAsync();

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/notify", "{}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(await ReadMessagesAsync(id));
        Assert.Equal(versionBefore, await ReadSnapshotVersionAsync());
        using var payload = await ReadPayloadAsync(id, "event.status_notified");
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("messageId").ValueKind);
    }

    [Fact]
    public async Task Deleting_the_message_before_the_send_renders_the_stock_paragraph()
    {
        var id = await CreateEventAsync(2106);
        var subscriberAddress = "deleted@wmsfo.test";
        await CreateVerifiedSubscriberAsync(await CreatePersonAsync(subscriberAddress), subscriberAddress);

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":true,\"message\":\"" + Text + "\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _host!.Outbox.RunOnceAsync(CancellationToken.None);

        var message = Assert.Single(await ReadMessagesAsync(id));
        var delete = await SendAdminAsync(HttpMethod.Delete, $"/admin/events/{id}/messages/{message.Id}", "");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var history = await ReadNewestHistoryAsync(id);
        Assert.Equal(JsonValueKind.Null, history.GetProperty("message").ValueKind);
        Assert.Equal(JsonValueKind.Null, history.GetProperty("messageId").ValueKind);

        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        var sent = Assert.Single(_host.Sender.Sent);
        var stock = EmailTemplates.StockParagraph(2, "Event 2106", Scheduled);
        Assert.Equal(stock, sent.Values["customMessage"]);
        var rendered = _templates!.Render(sent.TemplateName, sent.Values);
        Assert.Contains(stock, rendered.Text);
        Assert.DoesNotContain(Text, rendered.Text);
    }

    [Fact]
    public async Task My_alerts_reports_the_message_id_of_a_status_alert()
    {
        var id = await CreateEventAsync(2107);
        var personId = await UpsertDevPersonAsync();
        await CreateVerifiedSubscriberAsync(personId, DevStaticTokens.PersonEmail);

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":true,\"message\":\"" + Text + "\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        var message = Assert.Single(await ReadMessagesAsync(id));

        using var req = _host.PersonRequest(HttpMethod.Get, "/me/alerts");
        var me = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var doc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("event_status", item.GetProperty("kind").GetString());
        Assert.Equal(2, item.GetProperty("statusId").GetInt32());
        Assert.Equal(message.Id, item.GetProperty("messageId").GetInt64());
    }

    // ---------- helpers ----------

    private sealed record MessageRow(long Id, string Body, DateTimeOffset? EventTime, string CreatedBy);

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    // A current event in status 1 with a scheduled time, so status 2 is allowed.
    private async Task<long> CreateEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var clr = new NpgsqlCommand("update event set is_current = false where is_current;", conn))
            await clr.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, scheduled_at, created_by, updated_at)
values ($1, $2, 1, true, $3, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = Scheduled });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
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

    // The DevStaticTokens person, so GET /me/alerts resolves to this row.
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

    private async Task CreateVerifiedSubscriberAsync(long personId, string address)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at)
values ($1, 'email', $2, $3, now());", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "wsu_" + Guid.NewGuid().ToString("N") });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<MessageRow>> ReadMessagesAsync(long eventId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select id, body, event_time, created_by from event_message where event_id = $1 order by id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<MessageRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new MessageRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3)));
        }
        return rows;
    }

    // The newest StatusHistory item from GET /admin/events/{id}/status-history.
    private async Task<JsonElement> ReadNewestHistoryAsync(long eventId)
    {
        var response = await SendAdminAsync(HttpMethod.Get, $"/admin/events/{eventId}/status-history", "");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("items")[0].Clone();
    }

    private async Task<JsonDocument> ReadPayloadAsync(long eventId, string topic)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
select payload::text from outbox
where topic = $1 and (payload->>'eventId')::bigint = $2
order by id desc limit 1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = topic });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        return JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync())!);
    }

    private async Task<JsonDocument> ReadAuditAfterAsync(long eventId, string action)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
select after::text from audit_log
where entity = 'event' and entity_id = $1 and action = $2
order by id desc limit 1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = eventId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = action });
        return JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync())!);
    }

    private async Task<long> ReadSnapshotVersionAsync() =>
        Convert.ToInt64(await ScalarAsync("select version from snapshot where id = 1;"));

    private async Task<JsonDocument> ReadSnapshotAsync()
    {
        var key = (string)await ScalarAsync("select s3_key from snapshot where id = 1;");
        var content = await _host!.Store.GetObjectAsync(key);
        Assert.NotNull(content);
        return JsonDocument.Parse(content!.Bytes);
    }

    private async Task<object> ScalarAsync(string sql, long? param = null)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (param is not null)
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = param.Value });
        return await cmd.ExecuteScalarAsync() ?? DBNull.Value;
    }
}

// A74 migration: rolling back to A71 recreates event_status_history.message
// from the referenced body; migrating forward again turns every history text
// into an event_message row the history references and swaps the status
// alert payloads to messageId.
public sealed class A74StatusMessageMigrationTests : IClassFixture<PostgresFixture>
{
    private const string BeforeA74 = "20261003034820_A71SeededCookies";
    private readonly PostgresFixture _fixture;

    public A74StatusMessageMigrationTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Backfill_moves_history_text_into_event_messages_and_down_restores_it()
    {
        var opts = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.MigrateConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(opts);
        var migrator = db.GetService<IMigrator>();
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        // A message that a history row references comes back as its text on Down.
        await ExecAsync(conn, @"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values (2110, 'Event 2110', 2, false, 'seed', now());
insert into event_message (event_id, body, created_by)
select id, 'Kept text', 'admin@wmsfo.test' from event where year = 2110;
insert into event_status_history (event_id, from_status_id, to_status_id, changed_by, notify, message_id)
select e.id, 1, 2, 'admin@wmsfo.test', false, m.id from event e join event_message m on m.event_id = e.id where e.year = 2110;");
        await migrator.MigrateAsync(BeforeA74);
        Assert.Equal("Kept text", await ScalarAsync(conn,
            "select h.message from event_status_history h join event e on e.id = h.event_id where e.year = 2110;"));

        // Rows as the pre-A74 schema wrote them.
        await ExecAsync(conn, @"
delete from event_status_history; delete from event_message; delete from event;
insert into event (year, name, status_id, is_current, created_by, updated_at)
values (2111, 'Event 2111', 2, false, 'seed', now());
insert into outbox (topic, payload)
select 'event.status_changed', jsonb_build_object('eventId', id, 'fromStatusId', 1, 'toStatusId', 2, 'notify', true, 'message', 'Old text')
from event where year = 2111;
insert into event_status_history (event_id, from_status_id, to_status_id, changed_by, changed_at, notify, message, outbox_id)
select e.id, 1, 2, 'admin@wmsfo.test', '2030-12-20T18:00:00Z', true, 'Old text', o.id
from event e, outbox o where e.year = 2111;
insert into event_status_history (event_id, from_status_id, to_status_id, changed_by, notify, message)
select id, 2, 2, 'admin@wmsfo.test', true, null from event where year = 2111;");

        await migrator.MigrateAsync();

        await using (var cmd = new NpgsqlCommand(@"
select m.event_id = h.event_id, m.body, m.event_time, m.created_by, m.created_at, m.updated_at, h.changed_at
from event_status_history h join event_message m on m.id = h.message_id;", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.Equal("Old text", reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.Equal("admin@wmsfo.test", reader.GetString(3));
            Assert.Equal(reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(4));
            Assert.Equal(reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(5));
            Assert.False(await reader.ReadAsync());
        }
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(conn, "select count(*) from event_message;")));
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(conn,
            "select count(*) from event_status_history where message_id is null;")));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(conn,
            "select count(*) from information_schema.columns where table_name = 'event_status_history' and column_name = 'message';")));

        using var payload = JsonDocument.Parse((string)await ScalarAsync(conn, "select payload::text from outbox;"));
        Assert.False(payload.RootElement.TryGetProperty("message", out _));
        Assert.Equal(
            Convert.ToInt64(await ScalarAsync(conn, "select id from event_message;")),
            payload.RootElement.GetProperty("messageId").GetInt64());
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync() ?? DBNull.Value;
    }
}
