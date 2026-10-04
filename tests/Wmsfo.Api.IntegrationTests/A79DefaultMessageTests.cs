using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Email;

namespace Wmsfo.Api.IntegrationTests;

// A79 acceptance: a status change without a message posts the stock
// paragraph as the event message; an alert carries its message.
//   - notify false and no message creates the row with the stock paragraph,
//     the history references it, and the snapshot's latestMessage is it
//   - an announce without a message creates no row
//   - GET /me/alerts carries `message` for a status alert with a typed
//     message, for one without (the stock paragraph), for a message alert
//     (the body), and null once the message row is deleted
public sealed class A79DefaultMessageTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Text = "Come out at 6 tonight!";
    private static readonly DateTimeOffset Scheduled = DateTimeOffset.Parse("2034-12-24T23:00:00Z");

    private readonly PostgresFixture _fixture;
    private A30Host? _host;

    public A79DefaultMessageTests(PostgresFixture fixture) { _fixture = fixture; }

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
    public async Task Status_change_without_message_and_notify_false_posts_the_stock_paragraph()
    {
        var id = await CreateEventAsync(2201);
        var subscriberAddress = "quiet@wmsfo.test";
        await CreateVerifiedSubscriberAsync(await CreatePersonAsync(subscriberAddress), subscriberAddress);

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":false}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stock = EmailTemplates.StockParagraph(2, "Event 2201", Scheduled);
        var message = Assert.Single(await ReadMessagesAsync(id));
        Assert.Equal(stock, message.Body);
        Assert.False(message.Notify);
        Assert.Equal(DevStaticTokens.AdminEmail, message.CreatedBy);

        var history = await ReadNewestHistoryAsync(id);
        Assert.False(history.GetProperty("notify").GetBoolean());
        Assert.Equal(message.Id, history.GetProperty("messageId").GetInt64());
        Assert.Equal(stock, history.GetProperty("message").GetString());

        using (var snapshot = await ReadSnapshotAsync())
        {
            var latest = snapshot.RootElement.GetProperty("event").GetProperty("latestMessage");
            Assert.Equal(message.Id, latest.GetProperty("id").GetInt64());
            Assert.Equal(stock, latest.GetProperty("body").GetString());
        }

        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_host.Sender.Sent);
    }

    [Fact]
    public async Task Announce_without_message_creates_no_row()
    {
        var id = await CreateEventAsync(2202);

        var response = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/notify", "{}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(await ReadMessagesAsync(id));
        var history = await ReadNewestHistoryAsync(id);
        Assert.Equal(JsonValueKind.Null, history.GetProperty("messageId").ValueKind);
    }

    [Fact]
    public async Task My_alerts_carry_the_message_of_each_alert()
    {
        var id = await CreateEventAsync(2203);
        var personId = await UpsertDevPersonAsync();
        await CreateVerifiedSubscriberAsync(personId, DevStaticTokens.PersonEmail);

        // A status alert with a typed message.
        var typed = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":2,\"notify\":true,\"message\":\"" + Text + "\"}");
        Assert.Equal(HttpStatusCode.OK, typed.StatusCode);
        await DeliverAsync();

        // A status alert without a message.
        var stockChange = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/status",
            "{\"statusId\":1,\"notify\":true}");
        Assert.Equal(HttpStatusCode.OK, stockChange.StatusCode);
        await DeliverAsync();

        // A message alert.
        var posted = await SendAdminAsync(HttpMethod.Post, $"/admin/events/{id}/messages",
            "{\"body\":\"Cookies are ready\",\"notify\":true}");
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        await DeliverAsync();

        var items = await ReadMyAlertsAsync();
        Assert.Equal(3, items.Count);
        var stock = EmailTemplates.StockParagraph(1, "Event 2203", Scheduled);
        var messageAlert = items.Single(i => i.GetProperty("kind").GetString() == "event_message");
        Assert.Equal("Cookies are ready", messageAlert.GetProperty("message").GetString());
        var statusAlerts = items.Where(i => i.GetProperty("kind").GetString() == "event_status").ToList();
        Assert.Equal(Text, statusAlerts.Single(i => i.GetProperty("statusId").GetInt32() == 2).GetProperty("message").GetString());
        var stockAlert = statusAlerts.Single(i => i.GetProperty("statusId").GetInt32() == 1);
        Assert.Equal(stock, stockAlert.GetProperty("message").GetString());

        // Deleting the referenced message leaves the alert with a null message.
        var delete = await SendAdminAsync(HttpMethod.Delete,
            $"/admin/events/{id}/messages/{stockAlert.GetProperty("messageId").GetInt64()}", "");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var after = (await ReadMyAlertsAsync())
            .Single(i => i.GetProperty("kind").GetString() == "event_status" && i.GetProperty("statusId").GetInt32() == 1);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("message").ValueKind);
    }

    private async Task DeliverAsync()
    {
        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
    }

    private async Task<List<JsonElement>> ReadMyAlertsAsync()
    {
        using var req = _host!.PersonRequest(HttpMethod.Get, "/me/alerts");
        var me = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var doc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    // ---------- helpers ----------

    private sealed record MessageRow(long Id, string Body, bool Notify, string CreatedBy);

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
            "select id, body, notify, created_by from event_message where event_id = $1 order by id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<MessageRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new MessageRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetBoolean(2),
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
