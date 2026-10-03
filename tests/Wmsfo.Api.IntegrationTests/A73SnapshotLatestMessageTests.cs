using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;

namespace Wmsfo.Api.IntegrationTests;

// Snapshot latest message acceptance (contracts 1.3 event.latestMessage):
//   - latestMessage is the newest message by created_at, then id, keeps its
//     four keys in order, and is the last key of event; event has no
//     messages key.
//   - no messages give null.
//   - a message POST, PATCH, and DELETE rebuild latestMessage.
//   - the snapshot schema validates the fixture and a built snapshot, and
//     rejects an event object carrying messages.
public sealed class A73SnapshotLatestMessageTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly string[] MessageKeys = { "id", "body", "eventTime", "createdAt" };
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2032-12-22T00:00:00Z");

    private readonly PostgresFixture _fixture;
    private A25Host? _host;

    public A73SnapshotLatestMessageTests(PostgresFixture fixture)
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
    public async Task Three_messages_give_the_newest_by_created_at_then_id_and_no_messages_key()
    {
        var eventId = await CreateEventAsync(2032);
        await InsertMessageAsync(eventId, "Oldest", BaseTime, eventTime: null);
        await InsertMessageAsync(eventId, "Tied lower id", BaseTime.AddMinutes(20), eventTime: null);
        var newest = await InsertMessageAsync(eventId, "Tied higher id", BaseTime.AddMinutes(20), eventTime: BaseTime.AddMinutes(19));

        using var doc = await ReadSnapshotAsync();
        var ev = doc.RootElement.GetProperty("event");
        Assert.False(ev.TryGetProperty("messages", out _));
        Assert.Equal("latestMessage", ev.EnumerateObject().Last().Name);
        var latest = ev.GetProperty("latestMessage");
        Assert.Equal(MessageKeys, latest.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(newest, latest.GetProperty("id").GetInt64());
        Assert.Equal("Tied higher id", latest.GetProperty("body").GetString());
        Assert.Equal(BaseTime.AddMinutes(19), latest.GetProperty("eventTime").GetDateTimeOffset());
        Assert.Equal(BaseTime.AddMinutes(20), latest.GetProperty("createdAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task No_messages_give_null_latestMessage()
    {
        await CreateEventAsync(2035);

        using var doc = await ReadSnapshotAsync();
        var ev = doc.RootElement.GetProperty("event");
        Assert.False(ev.TryGetProperty("messages", out _));
        Assert.Equal(JsonValueKind.Null, ev.GetProperty("latestMessage").ValueKind);
    }

    [Fact]
    public async Task Message_post_patch_and_delete_rebuild_latestMessage()
    {
        var eventId = await CreateEventAsync(2036);
        // Created before now(), so the posted message is newer.
        var older = await InsertMessageAsync(eventId, "Older", DateTimeOffset.UtcNow.AddDays(-1), eventTime: null);

        var post = await SendAsync(HttpMethod.Post, $"/admin/events/{eventId}/messages",
            "{\"body\":\"Posted\",\"eventTime\":null,\"notify\":false}", DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        long posted;
        using (var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
            posted = created.RootElement.GetProperty("id").GetInt64();
        using (var afterPost = await ReadCurrentSnapshotAsync())
        {
            var latest = afterPost.RootElement.GetProperty("event").GetProperty("latestMessage");
            Assert.Equal(posted, latest.GetProperty("id").GetInt64());
            Assert.Equal("Posted", latest.GetProperty("body").GetString());
        }

        var patch = await SendAsync(HttpMethod.Patch, $"/admin/events/{eventId}/messages/{posted}",
            "{\"body\":\"Posted, edited\"}", DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using (var afterPatch = await ReadCurrentSnapshotAsync())
        {
            var latest = afterPatch.RootElement.GetProperty("event").GetProperty("latestMessage");
            Assert.Equal(posted, latest.GetProperty("id").GetInt64());
            Assert.Equal("Posted, edited", latest.GetProperty("body").GetString());
        }

        var delete = await SendAsync(HttpMethod.Delete, $"/admin/events/{eventId}/messages/{posted}", null, DevStaticTokens.AdminToken);
        Assert.True(delete.IsSuccessStatusCode, $"delete answered {(int)delete.StatusCode}");
        using (var afterDelete = await ReadCurrentSnapshotAsync())
        {
            var latest = afterDelete.RootElement.GetProperty("event").GetProperty("latestMessage");
            Assert.Equal(older, latest.GetProperty("id").GetInt64());
        }
    }

    [Fact]
    public async Task Schema_validates_the_fixture_and_a_built_snapshot_and_rejects_messages_on_event()
    {
        var eventId = await CreateEventAsync(2037);
        await InsertMessageAsync(eventId, "One", BaseTime, eventTime: BaseTime);

        var schemaText = File.ReadAllText(Path.Combine(TestPaths.ContractsDir, "schema", "snapshot.schema.json"));
        var schema = JsonSchema.FromText(schemaText);
        using (var schemaDoc = JsonDocument.Parse(schemaText))
        {
            var eventSchema = schemaDoc.RootElement.GetProperty("properties").GetProperty("event");
            Assert.False(eventSchema.GetProperty("properties").TryGetProperty("messages", out _));
            Assert.False(eventSchema.GetProperty("additionalProperties").GetBoolean());
        }

        var fixtureText = File.ReadAllText(Path.Combine(TestPaths.ContractsDir, "fixtures", "snapshot.json"));
        using (var fixture = JsonDocument.Parse(fixtureText))
        {
            AssertValid(schema, fixture.RootElement, "fixture");
            Assert.False(fixture.RootElement.GetProperty("event").TryGetProperty("messages", out _));
        }

        using (var built = await ReadSnapshotAsync())
            AssertValid(schema, built.RootElement, "built snapshot");

        var withMessages = JsonNode.Parse(fixtureText)!;
        var latestCopy = withMessages["event"]!["latestMessage"]!.DeepClone();
        withMessages["event"]!["messages"] = new JsonArray(latestCopy);
        using var rejected = JsonDocument.Parse(withMessages.ToJsonString());
        Assert.False(schema.Evaluate(rejected.RootElement).IsValid);
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
