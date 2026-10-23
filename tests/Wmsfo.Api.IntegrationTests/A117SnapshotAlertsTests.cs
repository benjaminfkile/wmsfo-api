using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Data;
using Wmsfo.Api.Email;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.IntegrationTests;

// A117 acceptance: the alerts ride in the snapshot (contracts 1.3
// event.alerts), and the per-person reads are gone.
//   - a status change with notify, a notify with and without a message, a
//     message post with notify, and a message delete each rebuild the
//     snapshot, and its alerts follow, newest first
//   - a status change without notify and a message post without notify
//     rebuild the snapshot and add no alert
//   - each alert carries the email's subject and the message body read at
//     build time; another event's alerts are not listed
//   - at most 50 alerts
//   - GET /me and GET /me/alerts answer 404
//   - the snapshot carries cookieLimitPerPerson from the setting, and
//     signUpsOpen and subscriptionsOpen true
public sealed class A117SnapshotAlertsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly string[] AlertKeys =
        { "id", "kind", "statusId", "messageId", "subject", "message", "createdAt" };
    private static readonly DateTimeOffset Scheduled = DateTimeOffset.Parse("2034-12-24T23:00:00Z");

    private readonly PostgresFixture _fixture;
    private A30Host? _host;

    public A117SnapshotAlertsTests(PostgresFixture fixture) { _fixture = fixture; }

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
    public async Task Status_changes_notify_and_message_writes_rebuild_and_the_alerts_follow()
    {
        var id = await CreateEventAsync(2301);
        Assert.Empty(await RebuildAndReadAlertsAsync());

        // A status change with notify: one status alert with the stock paragraph.
        var version = await ReadSnapshotVersionAsync();
        var changed = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":true}");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.True(await ReadSnapshotVersionAsync() > version);
        var alerts = await ReadAlertsAsync();
        var statusAlert = Assert.Single(alerts);
        Assert.Equal(AlertKeys, statusAlert.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("event_status", statusAlert.GetProperty("kind").GetString());
        Assert.Equal(2, statusAlert.GetProperty("statusId").GetInt32());
        Assert.Equal("Santa's flight is scheduled", statusAlert.GetProperty("subject").GetString());
        Assert.Equal(EmailTemplates.StockParagraph(2, "Event 2301"), statusAlert.GetProperty("message").GetString());
        var stockMessageId = statusAlert.GetProperty("messageId").GetInt64();
        Assert.Equal(await ReadOutboxIdAsync("event.status_changed", id), statusAlert.GetProperty("id").GetInt64());

        // A status change without notify rebuilds (latestMessage moves) and adds no alert.
        version = await ReadSnapshotVersionAsync();
        var quiet = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":6,\"notify\":false,\"message\":\"Fog in the valley\"}");
        Assert.Equal(HttpStatusCode.OK, quiet.StatusCode);
        Assert.True(await ReadSnapshotVersionAsync() > version);
        using (var snap = await CurrentSnapshot.ReadAsync(_fixture.ConnectionString, _host!.Store))
        {
            var ev = snap.RootElement.GetProperty("event");
            Assert.Equal("Fog in the valley", ev.GetProperty("latestMessage").GetProperty("body").GetString());
            Assert.Single(ev.GetProperty("alerts").EnumerateArray());
        }

        // A notify without a message rebuilds and adds a status alert with no message.
        version = await ReadSnapshotVersionAsync();
        var announce = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/notify", "{}");
        Assert.Equal(HttpStatusCode.OK, announce.StatusCode);
        Assert.True(await ReadSnapshotVersionAsync() > version);
        alerts = await ReadAlertsAsync();
        Assert.Equal(2, alerts.Count);
        var announced = alerts[0];
        Assert.Equal("event_status", announced.GetProperty("kind").GetString());
        Assert.Equal(6, announced.GetProperty("statusId").GetInt32());
        Assert.Equal("Santa's flight is postponed", announced.GetProperty("subject").GetString());
        Assert.Equal(JsonValueKind.Null, announced.GetProperty("messageId").ValueKind);
        Assert.Equal(JsonValueKind.Null, announced.GetProperty("message").ValueKind);

        // A notify with a message carries it.
        var announceText = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/notify",
            "{\"message\":\"New date soon\"}");
        Assert.Equal(HttpStatusCode.OK, announceText.StatusCode);
        alerts = await ReadAlertsAsync();
        Assert.Equal(3, alerts.Count);
        Assert.Equal("New date soon", alerts[0].GetProperty("message").GetString());

        // A message post without notify rebuilds and adds no alert.
        version = await ReadSnapshotVersionAsync();
        var silent = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"Only on the site\",\"notify\":false}");
        Assert.Equal(HttpStatusCode.Created, silent.StatusCode);
        Assert.True(await ReadSnapshotVersionAsync() > version);
        Assert.Equal(3, (await ReadAlertsAsync()).Count);

        // A message post with notify adds a message alert.
        var body = "Cookies are ready at the hangar, come early and bring a thermos of cocoa for the line";
        var posted = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            JsonSerializer.Serialize(new { body, notify = true }));
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        long postedId;
        using (var created = JsonDocument.Parse(await posted.Content.ReadAsStringAsync()))
            postedId = created.RootElement.GetProperty("id").GetInt64();
        alerts = await ReadAlertsAsync();
        Assert.Equal(4, alerts.Count);
        var messageAlert = alerts[0];
        Assert.Equal("event_message", messageAlert.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, messageAlert.GetProperty("statusId").ValueKind);
        Assert.Equal(postedId, messageAlert.GetProperty("messageId").GetInt64());
        Assert.Equal(body, messageAlert.GetProperty("message").GetString());
        Assert.Equal("Santa update: " + EmailTemplates.SubjectPreview(body), messageAlert.GetProperty("subject").GetString());
        Assert.Equal(EmailTemplates.MessageAlertSubject(body), messageAlert.GetProperty("subject").GetString());

        // Newest first: the ids descend.
        var ids = alerts.Select(a => a.GetProperty("id").GetInt64()).ToArray();
        Assert.Equal(ids.OrderByDescending(x => x).ToArray(), ids);

        // Deleting the posted message drops its alert; deleting a status
        // alert's message leaves the alert with a null message.
        var deletePosted = await SendAdminAsync(HttpMethod.Delete, $"/admin/events/{id}/messages/{postedId}", "");
        Assert.Equal(HttpStatusCode.NoContent, deletePosted.StatusCode);
        alerts = await ReadAlertsAsync();
        Assert.Equal(3, alerts.Count);
        Assert.DoesNotContain(alerts, a => a.GetProperty("kind").GetString() == "event_message");

        var deleteStock = await SendAdminAsync(HttpMethod.Delete, $"/admin/events/{id}/messages/{stockMessageId}", "");
        Assert.Equal(HttpStatusCode.NoContent, deleteStock.StatusCode);
        alerts = await ReadAlertsAsync();
        Assert.Equal(3, alerts.Count);
        var first = alerts[^1];
        Assert.Equal(2, first.GetProperty("statusId").GetInt32());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("message").ValueKind);
    }

    [Fact]
    public async Task Alerts_are_capped_at_50_and_list_only_the_current_event()
    {
        var other = await CreateEventAsync(2302);
        await InsertMessagePostedAlertsAsync(other, 3);
        var id = await CreateEventAsync(2303);
        await InsertMessagePostedAlertsAsync(id, 55);

        var alerts = await RebuildAndReadAlertsAsync();
        Assert.Equal(50, alerts.Count);
        var ids = alerts.Select(a => a.GetProperty("id").GetInt64()).ToArray();
        Assert.Equal(ids.OrderByDescending(x => x).ToArray(), ids);
        var newest = await ReadOutboxIdAsync("event.message_posted", id);
        Assert.Equal(newest, ids[0]);
        Assert.All(alerts, a => Assert.Equal("event_message", a.GetProperty("kind").GetString()));
    }

    // GET /me came back in A135 as the person's state; /me/alerts stays gone.
    [Fact]
    public async Task Removed_me_alerts_route_is_404()
    {
        using var req = _host!.PersonRequest(HttpMethod.Get, "/me/alerts");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Snapshot_carries_the_cookie_limit_and_the_two_switches()
    {
        await CreateEventAsync(2304);
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
insert into app_setting (key, value, updated_by, updated_at) values ('cookie_limit_per_person', '7'::jsonb, 'test', now())
on conflict (key) do update set value = excluded.value;", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await _host!.App.Services.GetRequiredService<SnapshotBuilder>().RebuildAsync(CancellationToken.None);

        using var snap = await CurrentSnapshot.ReadAsync(_fixture.ConnectionString, _host.Store);
        var root = snap.RootElement;
        Assert.Equal(7, root.GetProperty("cookieLimitPerPerson").GetInt32());
        Assert.True(root.GetProperty("signUpsOpen").GetBoolean());
        Assert.True(root.GetProperty("subscriptionsOpen").GetBoolean());
        Assert.Equal(new[] { "trackerThemes", "cookieLimitPerPerson", "signUpsOpen", "subscriptionsOpen" },
            root.EnumerateObject().Select(p => p.Name).TakeLast(4).ToArray());
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private Task<List<JsonElement>> ReadAlertsAsync() =>
        CurrentSnapshot.ReadAlertsAsync(_fixture.ConnectionString, _host!.Store);

    private async Task<List<JsonElement>> RebuildAndReadAlertsAsync()
    {
        await _host!.App.Services.GetRequiredService<SnapshotBuilder>().RebuildAsync(CancellationToken.None);
        return await ReadAlertsAsync();
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

    // One message with notify and its event.message_posted outbox row per alert.
    private async Task InsertMessagePostedAlertsAsync(long eventId, int count)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        for (var i = 0; i < count; i++)
        {
            await using var cmd = new NpgsqlCommand(@"
with m as (
  insert into event_message (event_id, body, created_by, notify) values ($1, $2, 'seed', true) returning id
), o as (
  insert into outbox (topic, payload)
  select 'event.message_posted', jsonb_build_object('eventId', $1, 'messageId', m.id) from m
  returning id
)
update event_message set outbox_id = (select id from o) where id = (select id from m);", conn);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Update {i}" });
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // The newest outbox row of a topic naming the event.
    private async Task<long> ReadOutboxIdAsync(string topic, long eventId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select max(id) from outbox where topic = $1 and (payload->>'eventId')::bigint = $2;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = topic });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<long> ReadSnapshotVersionAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select version from snapshot where id = 1;", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
