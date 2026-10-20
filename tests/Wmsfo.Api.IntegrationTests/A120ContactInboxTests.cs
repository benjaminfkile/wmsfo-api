using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Chores;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// A120: POST /contact writes one inbox object and no row (contracts 4.3,
// sql.md 8.13); the leader's ContactInboxImporter moves at most 100 objects a
// run into contact_message and the outbox and deletes them, deletes malformed
// objects, and inserts nothing for an object a previous run committed.
public sealed class A120ContactInboxTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Regex KeyPattern = new(@"^contact-inbox/\d{8}T\d{9}Z-[0-9a-f]{16}\.json$");

    private readonly PostgresFixture _fixture;
    private A12Host? _host;

    public A120ContactInboxTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        await A12MeEndpointsTests.MigrateAndCleanAsync(_fixture.ConnectionString);
        _host = await A12Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Contact_writes_one_private_object_and_no_row()
    {
        var response = await PostContactAsync("Jane", "jane@example.com", "Hi there");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "receivedAt" }, body.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        var receivedAt = body.RootElement.GetProperty("receivedAt").GetString();

        var key = Assert.Single(await InboxKeysAsync());
        Assert.Matches(KeyPattern, key);
        var head = await _host!.Store.HeadObjectAsync(key);
        Assert.Equal("application/json", head!.ContentType);
        Assert.Equal("private, no-store", head.CacheControl);
        Assert.Null(_host.Store.TagOf(key));

        var bytes = (await _host.Store.GetObjectAsync(key))!.Bytes;
        var inbox = ContactInbox.TryParse(bytes)!;
        Assert.Equal(bytes, CanonicalJson.SerializeToUtf8Bytes(inbox));
        Assert.Equal("Jane", inbox.Name);
        Assert.Equal("jane@example.com", inbox.Email);
        Assert.Equal("Hi there", inbox.Message);
        Assert.False(string.IsNullOrEmpty(inbox.ClientIp));
        using (var obj = JsonDocument.Parse(bytes))
            Assert.Equal(receivedAt, obj.RootElement.GetProperty("receivedAt").GetString());
        Assert.StartsWith("contact-inbox/" + inbox.ReceivedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'"), key);

        Assert.Equal(0, await CountAsync("select count(*) from contact_message;"));
        Assert.Equal(0, await CountAsync("select count(*) from outbox;"));
    }

    [Fact]
    public async Task Contact_put_failure_is_502_contact_write_failed()
    {
        _host!.Store.FailPut = true;
        try
        {
            var response = await PostContactAsync("Jane", "jane@example.com", "Hi there");
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("contact_write_failed", body.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            _host.Store.FailPut = false;
        }
        Assert.Empty(await InboxKeysAsync());
    }

    [Fact]
    public async Task Contact_invalid_body_is_400_and_writes_nothing()
    {
        var response = await PostContactAsync("", "nope", "");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fields = body.RootElement.GetProperty("details").GetProperty("fields");
        foreach (var field in new[] { "name", "email", "message" })
            Assert.True(fields.TryGetProperty(field, out _), field);
        Assert.Empty(await InboxKeysAsync());
    }

    [Fact]
    public async Task Import_inserts_the_row_and_the_outbox_row_and_deletes_the_object()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await PostContactAsync("Sam", "sam@example.com", "Hello")).StatusCode);
        var key = Assert.Single(await InboxKeysAsync());
        var inbox = ContactInbox.TryParse((await _host!.Store.GetObjectAsync(key))!.Bytes)!;

        var result = await Importer(_host.Store).RunOnceAsync(CancellationToken.None);
        Assert.Equal(new ContactInboxImporter.RunResult(1, 1, 0, 0, 0), result);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        long id;
        await using (var cmd = new NpgsqlCommand("select id, name, email, body, client_ip, created_at from contact_message;", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            id = reader.GetInt64(0);
            Assert.Equal("Sam", reader.GetString(1));
            Assert.Equal("sam@example.com", reader.GetString(2));
            Assert.Equal("Hello", reader.GetString(3));
            Assert.Equal(inbox.ClientIp, reader.GetString(4));
            Assert.Equal(inbox.ReceivedAt, reader.GetFieldValue<DateTimeOffset>(5));
            Assert.False(await reader.ReadAsync());
        }
        await using (var cmd = new NpgsqlCommand("select payload->>'contactMessageId' from outbox where topic = 'contact.received';", conn))
        {
            Assert.Equal(id.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)await cmd.ExecuteScalarAsync());
        }
        Assert.Empty(await InboxKeysAsync());
    }

    [Fact]
    public async Task A_malformed_object_is_deleted_and_inserts_nothing()
    {
        await SeedAsync("contact-inbox/20261220T010000000Z-00000000000000aa.json", Encoding.UTF8.GetBytes("{not json"));
        await SeedAsync("contact-inbox/20261220T010000001Z-00000000000000bb.json",
            Encoding.UTF8.GetBytes("{\"name\":\"\",\"email\":\"x@example.com\",\"message\":\"m\",\"clientIp\":\"\",\"receivedAt\":\"2026-12-20T01:00:00.001Z\"}"));

        var result = await Importer(_host!.Store).RunOnceAsync(CancellationToken.None);
        Assert.Equal(new ContactInboxImporter.RunResult(2, 0, 0, 2, 0), result);
        Assert.Empty(await InboxKeysAsync());
        Assert.Equal(0, await CountAsync("select count(*) from contact_message;"));
        Assert.Equal(0, await CountAsync("select count(*) from outbox;"));
    }

    [Fact]
    public async Task A_rerun_over_an_imported_object_inserts_nothing()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await PostContactAsync("Robin", "robin@example.com", "Hi")).StatusCode);
        var key = Assert.Single(await InboxKeysAsync());

        // The delete after the commit fails: the object stays.
        var first = await Importer(new FailingDeleteStore(_host!.Store)).RunOnceAsync(CancellationToken.None);
        Assert.Equal(new ContactInboxImporter.RunResult(1, 1, 0, 0, 0), first);
        Assert.Equal(new[] { key }, await InboxKeysAsync());

        var second = await Importer(_host.Store).RunOnceAsync(CancellationToken.None);
        Assert.Equal(new ContactInboxImporter.RunResult(1, 0, 1, 0, 0), second);
        Assert.Empty(await InboxKeysAsync());
        Assert.Equal(1, await CountAsync("select count(*) from contact_message;"));
        Assert.Equal(1, await CountAsync("select count(*) from outbox where topic = 'contact.received';"));
    }

    [Fact]
    public async Task A_database_failure_keeps_the_object()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await PostContactAsync("Kim", "kim@example.com", "Hi")).StatusCode);
        var broken = Wmsfo.Api.Config.WmsfoConnectionStrings.ForTests("Host=127.0.0.1;Port=1;Username=nobody;Timeout=1");
        var importer = new ContactInboxImporter(broken, _host!.Store, NullLogger<ContactInboxImporter>.Instance);

        var result = await importer.RunOnceAsync(CancellationToken.None);
        Assert.Equal(new ContactInboxImporter.RunResult(1, 0, 0, 0, 1), result);
        Assert.Single(await InboxKeysAsync());
    }

    [Fact]
    public async Task One_run_imports_the_oldest_100_objects()
    {
        var start = new DateTimeOffset(2026, 12, 20, 1, 0, 0, TimeSpan.Zero);
        var keys = new List<string>();
        for (var i = 0; i < 105; i++)
        {
            var at = start.AddMilliseconds(i * 7);
            var key = ContactInbox.KeyFor(at);
            keys.Add(key);
            await SeedAsync(key, CanonicalJson.SerializeToUtf8Bytes(new ContactInboxObject
            {
                Name = "Visitor " + i,
                Email = $"v{i}@example.com",
                Message = "Message " + i,
                ClientIp = "203.0.113.7",
                ReceivedAt = at,
            }));
        }

        var first = await Importer(_host!.Store).RunOnceAsync(CancellationToken.None);
        Assert.Equal(new ContactInboxImporter.RunResult(100, 100, 0, 0, 0), first);
        Assert.Equal(keys.Skip(100).ToArray(), await InboxKeysAsync());
        Assert.Equal(100, await CountAsync("select count(*) from contact_message;"));
        Assert.Equal(100, await CountAsync("select count(*) from outbox where topic = 'contact.received';"));
        Assert.Equal(0, await CountAsync("select count(*) from contact_message where email in ('v100@example.com', 'v104@example.com');"));

        var second = await Importer(_host.Store).RunOnceAsync(CancellationToken.None);
        Assert.Equal(new ContactInboxImporter.RunResult(5, 5, 0, 0, 0), second);
        Assert.Empty(await InboxKeysAsync());
        Assert.Equal(105, await CountAsync("select count(*) from contact_message;"));
    }

    private ContactInboxImporter Importer(IObjectStore store) =>
        new(TestConnections.For(_fixture.ConnectionString), store, NullLogger<ContactInboxImporter>.Instance);

    private async Task<HttpResponseMessage> PostContactAsync(string name, string email, string message)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/contact");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { name, email, message }), Encoding.UTF8, "application/json");
        return await _host!.Client.SendAsync(req);
    }

    private Task SeedAsync(string key, byte[] bytes) =>
        _host!.Store.PutObjectAsync(key, bytes, ContactInbox.ContentType, ContactInbox.CacheControl);

    private async Task<string[]> InboxKeysAsync()
    {
        var keys = new List<string>();
        await foreach (var entry in _host!.Store.ListPrefixAsync(ContactInbox.Prefix)) keys.Add(entry.Key);
        return keys.ToArray();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // Answers like the inner store except that every delete throws.
    private sealed class FailingDeleteStore(IObjectStore inner) : IObjectStore
    {
        public Task PutObjectAsync(string key, ReadOnlyMemory<byte> bytes, string contentType, string cacheControl, string? tag = null, CancellationToken cancellationToken = default) =>
            inner.PutObjectAsync(key, bytes, contentType, cacheControl, tag, cancellationToken);
        public Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default) =>
            throw new IOException("simulated delete failure");
        public IAsyncEnumerable<ObjectListEntry> ListPrefixAsync(string prefix, CancellationToken cancellationToken = default) =>
            inner.ListPrefixAsync(prefix, cancellationToken);
        public Task PutObjectTaggingAsync(string key, string tag, CancellationToken cancellationToken = default) =>
            inner.PutObjectTaggingAsync(key, tag, cancellationToken);
        public Task DeleteObjectTaggingAsync(string key, CancellationToken cancellationToken = default) =>
            inner.DeleteObjectTaggingAsync(key, cancellationToken);
        public Task<string?> GetObjectTaggingAsync(string key, CancellationToken cancellationToken = default) =>
            inner.GetObjectTaggingAsync(key, cancellationToken);
        public Task<ObjectHead?> HeadObjectAsync(string key, CancellationToken cancellationToken = default) =>
            inner.HeadObjectAsync(key, cancellationToken);
        public Task<ObjectContent?> GetObjectAsync(string key, CancellationToken cancellationToken = default) =>
            inner.GetObjectAsync(key, cancellationToken);
        public string PresignPut(string key, string contentType, string tag) => inner.PresignPut(key, contentType, tag);
    }
}
