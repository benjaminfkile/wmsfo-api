using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Data;

namespace Wmsfo.Api.IntegrationTests;

// A82 acceptance: a message's time is its createdAt, and a message posted
// with notify counts its emails on its own row.
//   - a post with notify true answers notify true and sentCount 0, and the
//     row carries the outbox id of its event.message_posted row
//   - the fan-out and the sender against one verified subscriber make GET
//     .../messages answer sentCount 1 and leave every history count as it was
//   - a post with eventTime in the body is 400, as is a patch with it
//   - a patch keeps notify and sentCount
//   - the snapshot's latestMessage carries exactly id, body, createdAt
public sealed class A82MessageNotifyTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Scheduled = DateTimeOffset.Parse("2034-12-24T23:00:00Z");

    private readonly PostgresFixture _fixture;
    private A30Host? _host;

    public A82MessageNotifyTests(PostgresFixture fixture) { _fixture = fixture; }

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
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Post_with_notify_answers_notify_and_zero_count_and_the_row_carries_the_outbox_id()
    {
        var id = await CreateEventAsync(2201);

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"Santa is over the valley.\",\"notify\":true}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var dto = doc.RootElement;
        Assert.True(dto.GetProperty("notify").GetBoolean());
        Assert.Equal(0, dto.GetProperty("sentCount").GetInt32());
        Assert.False(dto.TryGetProperty("eventTime", out _));
        var messageId = dto.GetProperty("id").GetInt64();

        var outboxId = Convert.ToInt64(await ScalarAsync(@"
select id from outbox
where topic = 'event.message_posted' and (payload->>'messageId')::bigint = $1;", messageId));
        Assert.Equal(outboxId, Convert.ToInt64(await ScalarAsync(
            "select outbox_id from event_message where id = $1;", messageId)));
        Assert.True((bool)await ScalarAsync("select notify from event_message where id = $1;", messageId));
    }

    [Fact]
    public async Task Post_without_notify_answers_notify_false_and_no_outbox_id()
    {
        var id = await CreateEventAsync(2202);

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"Quiet note.\",\"notify\":false}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("notify").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("sentCount").GetInt32());
        Assert.Equal(DBNull.Value, await ScalarAsync(
            "select outbox_id from event_message where id = $1;", doc.RootElement.GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task Fan_out_and_send_count_the_email_on_the_message_and_leave_history_counts()
    {
        var id = await CreateEventAsync(2203);
        await CreateVerifiedSubscriberAsync("count@wmsfo.test");

        // A status alert first, so a history row has a count of its own.
        var change = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":true}");
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        var historyBefore = await ReadHistoryCountsAsync();
        Assert.Contains(historyBefore, h => h.SentCount == 1);

        var post = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"Look up at 6.\",\"notify\":true}");
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        long messageId;
        using (var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
            messageId = created.RootElement.GetProperty("id").GetInt64();

        await _host.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, _host.Sender.Sent.Count);

        var list = await SendAdminAsync(HttpMethod.Get, $"/admin/events/{id}/messages", "");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var posted = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(m => m.GetProperty("id").GetInt64() == messageId);
        Assert.True(posted.GetProperty("notify").GetBoolean());
        Assert.Equal(1, posted.GetProperty("sentCount").GetInt32());

        // The status change's own message row is not the alert's owner.
        var statusMessage = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(m => m.GetProperty("id").GetInt64() != messageId);
        Assert.False(statusMessage.GetProperty("notify").GetBoolean());
        Assert.Equal(0, statusMessage.GetProperty("sentCount").GetInt32());

        Assert.Equal(historyBefore, await ReadHistoryCountsAsync());
    }

    [Fact]
    public async Task Event_time_in_a_post_or_a_patch_is_400()
    {
        var id = await CreateEventAsync(2204);

        var post = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"Timed\",\"eventTime\":\"2034-12-24T23:00:00.000Z\",\"notify\":false}");
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        var postNull = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"Timed\",\"eventTime\":null,\"notify\":false}");
        Assert.Equal(HttpStatusCode.BadRequest, postNull.StatusCode);
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(
            "select count(*) from event_message where event_id = $1;", id)));

        var created = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"Plain\",\"notify\":false}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var messageId = doc.RootElement.GetProperty("id").GetInt64();
        var patch = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}/messages/{messageId}",
            "{\"eventTime\":\"2034-12-24T23:00:00.000Z\"}");
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
    }

    [Fact]
    public async Task Patch_keeps_notify_and_sent_count()
    {
        var id = await CreateEventAsync(2205);
        var post = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"Before\",\"notify\":true}");
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        long messageId;
        using (var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
            messageId = created.RootElement.GetProperty("id").GetInt64();
        await ExecAsync("update event_message set sent_count = 3 where id = $1;", messageId);

        var patch = await SendAdminAsync(HttpMethod.Patch, $"/admin/events/{id}/messages/{messageId}",
            "{\"body\":\"After\"}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var doc = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        Assert.Equal("After", doc.RootElement.GetProperty("body").GetString());
        Assert.True(doc.RootElement.GetProperty("notify").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("sentCount").GetInt32());
        Assert.NotEqual(DBNull.Value, await ScalarAsync(
            "select outbox_id from event_message where id = $1;", messageId));
    }

    [Fact]
    public async Task Snapshot_latest_message_carries_exactly_id_body_created_at()
    {
        var id = await CreateEventAsync(2206);
        var post = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"Newest\",\"notify\":true}");
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        long messageId;
        using (var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
            messageId = created.RootElement.GetProperty("id").GetInt64();

        var key = (string)await ScalarAsync("select s3_key from snapshot where id = 1;");
        var content = await _host!.Store.GetObjectAsync(key);
        Assert.NotNull(content);
        using var snapshot = JsonDocument.Parse(content!.Bytes);
        var latest = snapshot.RootElement.GetProperty("event").GetProperty("latestMessage");
        Assert.Equal(new[] { "id", "body", "createdAt" }, latest.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(messageId, latest.GetProperty("id").GetInt64());
        Assert.Equal("Newest", latest.GetProperty("body").GetString());
    }

    // ---------- helpers ----------

    private sealed record HistoryCount(long Id, int SentCount);

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

    private async Task CreateVerifiedSubscriberAsync(string address)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
with p as (insert into person (cognito_sub, email) values (gen_random_uuid(), $1) returning id)
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at)
select p.id, 'email', $1, $2, now() from p;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "wsu_" + Guid.NewGuid().ToString("N") });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<HistoryCount>> ReadHistoryCountsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select id, sent_count from event_status_history order by id;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<HistoryCount>();
        while (await reader.ReadAsync())
            rows.Add(new HistoryCount(reader.GetInt64(0), reader.GetInt32(1)));
        return rows;
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

    private async Task ExecAsync(string sql, long param)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = param });
        await cmd.ExecuteNonQueryAsync();
    }
}

// A82 migration: on a database with an event.message_posted outbox row, the
// message its payload names takes notify true, the outbox id, and the count
// of emails already sent for it; any other message stays notify false. Down
// recreates event_time nullable.
public sealed class A82MessageNotifyMigrationTests : IClassFixture<PostgresFixture>
{
    private const string BeforeA82 = "20261003171223_A77Postponed";
    private readonly PostgresFixture _fixture;

    public A82MessageNotifyMigrationTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Backfill_marks_the_posted_message_notify_and_down_restores_event_time()
    {
        var opts = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.MigrateConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(opts);
        var migrator = db.GetService<IMigrator>();
        await db.Database.MigrateAsync();
        await migrator.MigrateAsync(BeforeA82);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        // Rows as the pre-A82 schema wrote them: a notified message with one
        // sent and one unsent delivery, and a quiet message.
        await ExecAsync(conn, @"
delete from alert_delivery; delete from outbox; delete from event_status_history;
delete from event_message; delete from event; delete from subscriber; delete from person;
insert into event (year, name, status_id, is_current, created_by, updated_at)
values (2210, 'Event 2210', 2, false, 'seed', now());
insert into event_message (event_id, body, event_time, created_by)
select id, 'Notified', '2030-12-20T18:00:00Z', 'admin@wmsfo.test' from event where year = 2210;
insert into event_message (event_id, body, event_time, created_by)
select id, 'Quiet', null, 'admin@wmsfo.test' from event where year = 2210;
insert into outbox (topic, payload)
select 'event.message_posted', jsonb_build_object('eventId', m.event_id, 'messageId', m.id)
from event_message m where m.body = 'Notified';
insert into outbox (topic, payload)
values ('event.message_posted', jsonb_build_object('eventId', 1, 'messageId', 987654321));
insert into person (cognito_sub, email) values (gen_random_uuid(), 'a@wmsfo.test'), (gen_random_uuid(), 'b@wmsfo.test');
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at)
select id, 'email', email, 'wsu_' || id, now() from person;
insert into alert_delivery (outbox_id, subscriber_id, sent_at)
select o.id, s.id, case when s.address = 'a@wmsfo.test' then now() else null end
from outbox o, subscriber s
where (o.payload->>'messageId')::bigint = (select id from event_message where body = 'Notified');");

        await migrator.MigrateAsync();

        await using (var cmd = new NpgsqlCommand(@"
select m.notify, m.outbox_id = o.id, m.sent_count
from event_message m
join outbox o on (o.payload->>'messageId')::bigint = m.id
where m.body = 'Notified';", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.False(await reader.ReadAsync());
        }
        await using (var cmd = new NpgsqlCommand(
            "select notify, outbox_id is null, sent_count from event_message where body = 'Quiet';", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(0, reader.GetInt32(2));
        }
        Assert.Equal(0L, await ColumnCountAsync(conn, "event_time"));

        await migrator.MigrateAsync(BeforeA82);
        Assert.Equal(1L, await ColumnCountAsync(conn, "event_time"));
        Assert.Equal(0L, await ColumnCountAsync(conn, "notify"));
        await using (var cmd = new NpgsqlCommand(
            "select is_nullable from information_schema.columns where table_name = 'event_message' and column_name = 'event_time';", conn))
            Assert.Equal("YES", (string)(await cmd.ExecuteScalarAsync())!);

        await migrator.MigrateAsync();
    }

    private static async Task<long> ColumnCountAsync(NpgsqlConnection conn, string column)
    {
        await using var cmd = new NpgsqlCommand(
            "select count(*) from information_schema.columns where table_name = 'event_message' and column_name = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = column });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
