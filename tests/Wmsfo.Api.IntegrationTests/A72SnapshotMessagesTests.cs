using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.IntegrationTests;

// Snapshot messages acceptance (contracts 1.3 event.messages):
//   - the current event's messages list newest first (created_at desc, id desc)
//     with latestMessage equal to the first; no messages give [] and null.
//   - the list caps at the newest SnapshotBuilder.SnapshotMessageCap.
//   - a message PATCH and a DELETE rebuild the list.
//   - the snapshot schema validates the fixture and a built snapshot, and a
//     rebuild of identical data writes the same object.
public sealed class A72SnapshotMessagesTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly string[] MessageKeys = { "id", "body", "eventTime", "createdAt" };
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2032-12-22T00:00:00Z");

    private readonly PostgresFixture _fixture;
    private A25Host? _host;

    public A72SnapshotMessagesTests(PostgresFixture fixture)
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
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
        _host = await A25Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Three_messages_list_newest_first_and_latestMessage_is_the_first()
    {
        var eventId = await CreateEventAsync(2032);
        var oldest = await InsertMessageAsync(eventId, "Oldest", BaseTime, eventTime: null);
        var newest = await InsertMessageAsync(eventId, "Newest", BaseTime.AddMinutes(20), eventTime: BaseTime.AddMinutes(19));
        var middle = await InsertMessageAsync(eventId, "Middle", BaseTime.AddMinutes(10), eventTime: null);

        using var doc = await ReadSnapshotAsync();
        var ev = doc.RootElement.GetProperty("event");
        var messages = ev.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(new[] { newest, middle, oldest }, messages.Select(m => m.GetProperty("id").GetInt64()).ToArray());
        Assert.Equal(new[] { "Newest", "Middle", "Oldest" }, messages.Select(m => m.GetProperty("body").GetString()).ToArray());
        foreach (var m in messages)
            Assert.Equal(MessageKeys, m.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, messages[2].GetProperty("eventTime").ValueKind);
        Assert.Equal(messages[0].GetRawText(), ev.GetProperty("latestMessage").GetRawText());
    }

    [Fact]
    public async Task Equal_created_at_orders_by_id_desc()
    {
        var eventId = await CreateEventAsync(2033);
        var first = await InsertMessageAsync(eventId, "First", BaseTime, eventTime: null);
        var second = await InsertMessageAsync(eventId, "Second", BaseTime, eventTime: null);

        using var doc = await ReadSnapshotAsync();
        var ids = doc.RootElement.GetProperty("event").GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("id").GetInt64()).ToArray();
        Assert.Equal(new[] { second, first }, ids);
    }

    [Fact]
    public async Task Fifty_one_messages_cap_at_the_newest_fifty()
    {
        var eventId = await CreateEventAsync(2034);
        var ids = new List<long>();
        for (var i = 0; i < 51; i++)
            ids.Add(await InsertMessageAsync(eventId, $"Message {i}", BaseTime.AddMinutes(i), eventTime: null));

        using var doc = await ReadSnapshotAsync();
        var ev = doc.RootElement.GetProperty("event");
        var listed = ev.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetInt64()).ToArray();
        Assert.Equal(SnapshotBuilder.SnapshotMessageCap, listed.Length);
        Assert.Equal(Enumerable.Reverse(ids).Take(50).ToArray(), listed);
        Assert.DoesNotContain(ids[0], listed);
        Assert.Equal(ids[50], ev.GetProperty("latestMessage").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task No_messages_give_an_empty_list_and_null_latestMessage()
    {
        await CreateEventAsync(2035);

        using var doc = await ReadSnapshotAsync();
        var ev = doc.RootElement.GetProperty("event");
        Assert.Equal(JsonValueKind.Array, ev.GetProperty("messages").ValueKind);
        Assert.Equal(0, ev.GetProperty("messages").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, ev.GetProperty("latestMessage").ValueKind);
    }

    [Fact]
    public async Task No_current_event_leaves_event_null()
    {
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("update event set is_current = false where is_current;", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        using var doc = await ReadSnapshotAsync();
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("event").ValueKind);
    }

    [Fact]
    public async Task Message_patch_and_delete_rebuild_the_list()
    {
        var eventId = await CreateEventAsync(2036);
        var older = await InsertMessageAsync(eventId, "Older", BaseTime, eventTime: null);
        var newer = await InsertMessageAsync(eventId, "Newer", BaseTime.AddMinutes(5), eventTime: null);

        var patch = await SendAsync(HttpMethod.Patch, $"/admin/events/{eventId}/messages/{older}",
            "{\"body\":\"Older, edited\"}", DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using (var afterPatch = await ReadCurrentSnapshotAsync())
        {
            var messages = afterPatch.RootElement.GetProperty("event").GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(new[] { newer, older }, messages.Select(m => m.GetProperty("id").GetInt64()).ToArray());
            Assert.Equal("Older, edited", messages[1].GetProperty("body").GetString());
        }

        var delete = await SendAsync(HttpMethod.Delete, $"/admin/events/{eventId}/messages/{newer}", null, DevStaticTokens.AdminToken);
        Assert.True(delete.IsSuccessStatusCode, $"delete answered {(int)delete.StatusCode}");
        using (var afterDelete = await ReadCurrentSnapshotAsync())
        {
            var ev = afterDelete.RootElement.GetProperty("event");
            var messages = ev.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(new[] { older }, messages.Select(m => m.GetProperty("id").GetInt64()).ToArray());
            Assert.Equal(older, ev.GetProperty("latestMessage").GetProperty("id").GetInt64());
        }
    }

    [Fact]
    public async Task Schema_validates_the_fixture_and_a_built_snapshot_and_rebuilds_are_identical()
    {
        var eventId = await CreateEventAsync(2037);
        await InsertMessageAsync(eventId, "One", BaseTime, eventTime: BaseTime);
        await InsertMessageAsync(eventId, "Two", BaseTime.AddMinutes(1), eventTime: null);

        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(TestPaths.ContractsDir, "schema", "snapshot.schema.json")));

        using (var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.ContractsDir, "fixtures", "snapshot.json"))))
        {
            AssertValid(schema, fixture.RootElement, "fixture");
            var ev = fixture.RootElement.GetProperty("event");
            Assert.Equal(2, ev.GetProperty("messages").GetArrayLength());
            Assert.Equal(ev.GetProperty("messages")[0].GetRawText(), ev.GetProperty("latestMessage").GetRawText());
        }

        using var built = await ReadSnapshotAsync();
        AssertValid(schema, built.RootElement, "built snapshot");

        var firstKey = await ReadSnapshotKeyAsync();
        using (await ReadSnapshotAsync()) { }
        Assert.Equal(firstKey, await ReadSnapshotKeyAsync());
    }

    private static void AssertValid(JsonSchema schema, JsonElement instance, string what)
    {
        var results = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid) return;
        var details = results.Details ?? Enumerable.Empty<EvaluationResults>();
        var messages = string.Join("\n", details
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => (d.Errors ?? new Dictionary<string, string>()).Select(e => $"{d.InstanceLocation}: {e.Key}={e.Value}")));
        Assert.Fail($"snapshot schema did not accept the {what}:\n{messages}");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, string token)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host!.Client.SendAsync(req);
    }

    private async Task<long> CreateEventAsync(int year)
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

    private async Task<long> InsertMessageAsync(long eventId, string body, DateTimeOffset createdAt, DateTimeOffset? eventTime)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event_message (event_id, body, event_time, created_by, created_at, updated_at)
values ($1, $2, $3, 'seed', $4, $4) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)eventTime?.UtcDateTime ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = createdAt.UtcDateTime });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    // Runs a [snapshot] write that changes nothing, then reads the object the
    // snapshot row points at.
    private async Task<JsonDocument> ReadSnapshotAsync()
    {
        var response = await SendAsync(HttpMethod.Put, "/admin/settings/poll_interval_ms", "{\"value\":5000}", DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadCurrentSnapshotAsync();
    }

    // Reads the object the snapshot row points at, without a write.
    private async Task<JsonDocument> ReadCurrentSnapshotAsync()
    {
        var content = await _host!.Store.GetObjectAsync(await ReadSnapshotKeyAsync());
        Assert.NotNull(content);
        return JsonDocument.Parse(content!.Bytes);
    }

    private async Task<string> ReadSnapshotKeyAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select s3_key from snapshot where id = 1;", conn);
        return (string)(await cmd.ExecuteScalarAsync() ?? "");
    }
}
