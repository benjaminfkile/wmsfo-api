using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Help;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// Help topics (contracts 4.5 Help, sql.md 3.32 and 8.16):
//   - the boot ensure writes one row per seed entry with the defaults shown
//   - a later ensure keeps an admin's edit, moves default_* and
//     default_updated_at when the seed changed (defaultChanged), and deletes
//     a key the seed no longer lists
//   - GET /admin/help (Editor) lists every row ordered by page then key
//   - PUT /admin/help/{key} (Admin) validates, stores, stamps, and audits
//   - POST /admin/help/{key}/reset (Admin) restores the defaults
//   - an API key needs the `help` capability
public sealed class A89HelpTopicTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly string EmDash = ((char)0x2014).ToString();

    private readonly PostgresFixture _fixture;
    private A26Host? _host;
    private HelpTopicSeed _seed = null!;

    public A89HelpTopicTests(PostgresFixture fixture)
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

        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (var sql in new[] { "delete from help_topic;", "delete from api_key;", "delete from audit_log where entity = 'help_topic';" })
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        _seed = HelpTopicSeed.Load(TestPaths.RepoRoot);
        _host = await A26Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Boot_ensure_writes_every_seed_row_with_the_defaults_shown()
    {
        var first = await EnsureAsync(_seed);
        Assert.Equal(120, first.Inserted);
        Assert.Equal(0, first.Updated);
        Assert.Equal(0, first.Deleted);

        Assert.Equal(120L, await ScalarAsync<long>("select count(*) from help_topic;"));
        Assert.Equal(0L, await ScalarAsync<long>(
            "select count(*) from help_topic where edited_by is not null or edited_at is not null;"));
        Assert.Equal(0L, await ScalarAsync<long>(
            "select count(*) from help_topic where title <> default_title or body <> default_body or links <> default_links;"));

        var entry = _seed.Entries.First(e => e.Links.Count > 0);
        Assert.Equal(entry.Title, await ScalarAsync<string>($"select title from help_topic where key = '{entry.Key}';"));
        Assert.Equal(entry.Links[0].To, await ScalarAsync<string>(
            $"select links->0->>'to' from help_topic where key = '{entry.Key}';"));

        // A second ensure with the same seed writes nothing.
        var stampBefore = await ScalarAsync<DateTime>("select max(updated_at) from help_topic;");
        var second = await EnsureAsync(_seed);
        Assert.Equal(new HelpTopics.EnsureResult(0, 0, 120, 0), second);
        Assert.Equal(stampBefore, await ScalarAsync<DateTime>("select max(updated_at) from help_topic;"));
    }

    [Fact]
    public async Task A_later_ensure_keeps_an_edit_and_moves_the_defaults()
    {
        await EnsureAsync(_seed);
        var edited = _seed.Entries[0];
        var plain = _seed.Entries[1];

        var put = await SendAsync(HttpMethod.Put, $"/admin/help/{edited.Key}",
            "{\"title\":\"Edited title\",\"body\":\"Edited body\",\"links\":[{\"label\":\"Media\",\"to\":\"/media\"}]}");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var defaultStamp = await ScalarAsync<DateTime>($"select default_updated_at from help_topic where key = '{edited.Key}';");

        // The same seed again leaves the edit and the default stamp alone.
        await EnsureAsync(_seed);
        Assert.Equal("Edited title", await ScalarAsync<string>($"select title from help_topic where key = '{edited.Key}';"));
        Assert.Equal(defaultStamp, await ScalarAsync<DateTime>($"select default_updated_at from help_topic where key = '{edited.Key}';"));
        Assert.False((await GetTopicAsync(edited.Key)).GetProperty("defaultChanged").GetBoolean());

        // A seed whose defaults changed moves default_* on both rows; the
        // edited row keeps its text, the unedited one follows the seed.
        var changed = Rewrite(_seed, nodes =>
        {
            foreach (var key in new[] { edited.Key, plain.Key })
            {
                var node = nodes.First(n => n!["key"]!.GetValue<string>() == key)!;
                node["title"] = "New default title";
                node["body"] = "New default body";
                node["links"] = new JsonArray(new JsonObject { ["label"] = "Audit", ["to"] = "/audit" });
            }
        });
        var result = await EnsureAsync(changed);
        Assert.Equal(2, result.Updated);
        Assert.Equal(118, result.Unchanged);

        Assert.Equal("Edited title", await ScalarAsync<string>($"select title from help_topic where key = '{edited.Key}';"));
        Assert.Equal("Edited body", await ScalarAsync<string>($"select body from help_topic where key = '{edited.Key}';"));
        Assert.Equal("/media", await ScalarAsync<string>($"select links->0->>'to' from help_topic where key = '{edited.Key}';"));
        Assert.Equal("New default title", await ScalarAsync<string>($"select default_title from help_topic where key = '{edited.Key}';"));
        Assert.Equal("New default body", await ScalarAsync<string>($"select default_body from help_topic where key = '{edited.Key}';"));
        Assert.Equal("/audit", await ScalarAsync<string>($"select default_links->0->>'to' from help_topic where key = '{edited.Key}';"));
        Assert.True(await ScalarAsync<DateTime>($"select default_updated_at from help_topic where key = '{edited.Key}';") > defaultStamp);

        var dto = await GetTopicAsync(edited.Key);
        Assert.True(dto.GetProperty("edited").GetBoolean());
        Assert.True(dto.GetProperty("defaultChanged").GetBoolean());

        Assert.Equal("New default title", await ScalarAsync<string>($"select title from help_topic where key = '{plain.Key}';"));
        Assert.Equal("/audit", await ScalarAsync<string>($"select links->0->>'to' from help_topic where key = '{plain.Key}';"));
        var plainDto = await GetTopicAsync(plain.Key);
        Assert.False(plainDto.GetProperty("edited").GetBoolean());
        Assert.False(plainDto.GetProperty("defaultChanged").GetBoolean());

        // Reset brings the new defaults in and clears defaultChanged.
        var reset = await SendAsync(HttpMethod.Post, $"/admin/help/{edited.Key}/reset", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        using var resetDoc = await ReadJsonAsync(reset);
        Assert.Equal("New default title", resetDoc.RootElement.GetProperty("title").GetString());
        Assert.False(resetDoc.RootElement.GetProperty("defaultChanged").GetBoolean());
    }

    [Fact]
    public async Task A_key_removed_from_the_seed_is_deleted()
    {
        await EnsureAsync(_seed);
        var gone = _seed.Entries[^1].Key;
        var shorter = Rewrite(_seed, nodes => nodes.RemoveAt(nodes.Count - 1));

        var result = await EnsureAsync(shorter);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(119, result.Unchanged);
        Assert.Equal(0L, await ScalarAsync<long>($"select count(*) from help_topic where key = '{gone}';"));
        Assert.Equal(119L, await ScalarAsync<long>("select count(*) from help_topic;"));
    }

    [Fact]
    public async Task Get_as_editor_answers_every_row_unedited_ordered_by_page_then_key()
    {
        await EnsureAsync(_seed);
        var response = await SendAsync(HttpMethod.Get, "/admin/help", null, DevStaticTokens.EditorToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(120, items.Count);

        var expected = _seed.Entries
            .OrderBy(e => e.Page, StringComparer.Ordinal)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => e.Key)
            .ToArray();
        Assert.Equal(expected, items.Select(i => i.GetProperty("key").GetString()).ToArray());

        foreach (var item in items)
        {
            Assert.False(item.GetProperty("edited").GetBoolean());
            Assert.False(item.GetProperty("defaultChanged").GetBoolean());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("editedBy").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("editedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("audit").ValueKind);
            Assert.Equal(
                new[] { "audit", "body", "defaultChanged", "edited", "editedAt", "editedBy", "key", "label", "links", "page", "title", "updatedAt" },
                item.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        var seeded = _seed.Entries.First(e => e.Links.Count > 0);
        var row = items.First(i => i.GetProperty("key").GetString() == seeded.Key);
        Assert.Equal(seeded.Title, row.GetProperty("title").GetString());
        Assert.Equal(seeded.Body, row.GetProperty("body").GetString());
        Assert.Equal(seeded.Links[0].Label, row.GetProperty("links")[0].GetProperty("label").GetString());

        var person = await SendAsync(HttpMethod.Get, "/admin/help", null, DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.Forbidden, person.StatusCode);
    }

    [Fact]
    public async Task Put_as_admin_validates_each_field()
    {
        await EnsureAsync(_seed);
        var key = _seed.Entries[0].Key;

        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Put, $"/admin/help/{key}",
            Body(new string('t', 121), "Body", "[]")), "title");
        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Put, $"/admin/help/{key}",
            Body("   ", "Body", "[]")), "title");
        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Put, $"/admin/help/{key}",
            Body("Title", new string('b', 2001), "[]")), "body");
        var seven = "[" + string.Join(",", Enumerable.Range(0, 7).Select(i => $"{{\"label\":\"L{i}\",\"to\":\"/p{i}\"}}")) + "]";
        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Put, $"/admin/help/{key}",
            Body("Title", "Body", seven)), "links");
        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Put, $"/admin/help/{key}",
            Body("Title", "Body", "[{\"label\":\"Go\",\"to\":\"ftp://x\"}]")), "links[0].to");
        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Put, $"/admin/help/{key}",
            Body("Title", "Body", "[{\"label\":\"" + new string('l', 61) + "\",\"to\":\"/x\"}]")), "links[0].label");
        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Put, $"/admin/help/{key}",
            Body("Title", "A body " + EmDash + " with a dash", "[]")), "body");
        await AssertValidationFailedOnAsync(await SendAsync(HttpMethod.Put, $"/admin/help/{key}",
            Body("A " + EmDash + " title", "Body", "[]")), "title");

        var unknownField = await SendAsync(HttpMethod.Put, $"/admin/help/{key}",
            "{\"title\":\"T\",\"body\":\"B\",\"links\":[],\"page\":\"x\"}");
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);

        // Nothing was stored and nothing was audited.
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from help_topic where edited_by is not null;"));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from audit_log where entity = 'help_topic';"));
    }

    [Fact]
    public async Task Put_as_admin_stores_trims_stamps_and_audits()
    {
        await EnsureAsync(_seed);
        var entry = _seed.Entries[0];
        var response = await SendAsync(HttpMethod.Put, $"/admin/help/{entry.Key}",
            Body("  New title  ", "  New body\n\nSecond paragraph.  ",
                "[{\"label\":\" Events \",\"to\":\" /events \"},{\"label\":\"Docs\",\"to\":\"https://example.com/help\"}]"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var doc = await ReadJsonAsync(response))
        {
            var root = doc.RootElement;
            Assert.Equal(entry.Key, root.GetProperty("key").GetString());
            Assert.Equal(entry.Page, root.GetProperty("page").GetString());
            Assert.Equal("New title", root.GetProperty("title").GetString());
            Assert.Equal("New body\n\nSecond paragraph.", root.GetProperty("body").GetString());
            var links = root.GetProperty("links").EnumerateArray().ToList();
            Assert.Equal(2, links.Count);
            Assert.Equal("Events", links[0].GetProperty("label").GetString());
            Assert.Equal("/events", links[0].GetProperty("to").GetString());
            Assert.True(root.GetProperty("edited").GetBoolean());
            Assert.False(root.GetProperty("defaultChanged").GetBoolean());
            Assert.Equal(DevStaticTokens.AdminEmail, root.GetProperty("editedBy").GetString());
            Assert.Equal(JsonValueKind.String, root.GetProperty("editedAt").ValueKind);
            Assert.Equal("update", root.GetProperty("audit").GetProperty("action").GetString());
        }

        Assert.Equal(DevStaticTokens.AdminEmail, await ScalarAsync<string>(
            $"select edited_by from help_topic where key = '{entry.Key}';"));
        Assert.True(await ScalarAsync<bool>($"select edited_at is not null from help_topic where key = '{entry.Key}';"));
        Assert.Equal(entry.Title, await ScalarAsync<string>($"select default_title from help_topic where key = '{entry.Key}';"));

        Assert.Equal(1L, await ScalarAsync<long>(
            $"select count(*) from audit_log where entity = 'help_topic' and entity_id = '{entry.Key}' and action = 'update';"));
        Assert.Equal(entry.Title, await ScalarAsync<string>(
            $"select before->>'title' from audit_log where entity = 'help_topic' and entity_id = '{entry.Key}';"));
        Assert.Equal("New title", await ScalarAsync<string>(
            $"select after->>'title' from audit_log where entity = 'help_topic' and entity_id = '{entry.Key}';"));

        using (var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/admin/help", null, DevStaticTokens.EditorToken)))
        {
            var items = list.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Single(items, i => i.GetProperty("edited").GetBoolean());
        }

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Put, "/admin/help/no.such-key", Body("T", "B", "[]"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Put, "/admin/help/Not_A_Key", Body("T", "B", "[]"))).StatusCode);
    }

    [Fact]
    public async Task Writes_with_an_editor_token_are_403()
    {
        await EnsureAsync(_seed);
        var key = _seed.Entries[0].Key;
        var put = await SendAsync(HttpMethod.Put, $"/admin/help/{key}", Body("T", "B", "[]"), DevStaticTokens.EditorToken);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        var reset = await SendAsync(HttpMethod.Post, $"/admin/help/{key}/reset", null, DevStaticTokens.EditorToken);
        Assert.Equal(HttpStatusCode.Forbidden, reset.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from help_topic where edited_by is not null;"));
    }

    [Fact]
    public async Task Reset_restores_the_defaults_and_clears_the_stamp()
    {
        await EnsureAsync(_seed);
        var entry = _seed.Entries.First(e => e.Links.Count > 0);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/help/{entry.Key}",
            Body("Edited", "Edited body", "[]"))).StatusCode);

        var reset = await SendAsync(HttpMethod.Post, $"/admin/help/{entry.Key}/reset", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        using (var doc = await ReadJsonAsync(reset))
        {
            var root = doc.RootElement;
            Assert.Equal(entry.Title, root.GetProperty("title").GetString());
            Assert.Equal(entry.Body, root.GetProperty("body").GetString());
            Assert.Equal(entry.Links.Count, root.GetProperty("links").GetArrayLength());
            Assert.False(root.GetProperty("edited").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("editedBy").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("editedAt").ValueKind);
            Assert.Equal("reset", root.GetProperty("audit").GetProperty("action").GetString());
        }
        Assert.True(await ScalarAsync<bool>(
            $"select edited_by is null and edited_at is null and title = default_title and body = default_body and links = default_links from help_topic where key = '{entry.Key}';"));
        Assert.Equal("Edited", await ScalarAsync<string>(
            $"select before->>'title' from audit_log where entity = 'help_topic' and entity_id = '{entry.Key}' and action = 'reset';"));

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Post, "/admin/help/no.such-key/reset", null)).StatusCode);
    }

    [Fact]
    public async Task An_api_key_needs_the_help_capability()
    {
        await EnsureAsync(_seed);
        var key = _seed.Entries[0].Key;
        var withHelp = await MintAsync("help-key", "[\"help\"]");
        var without = await MintAsync("sponsors-key", "[\"sponsors\"]");

        Assert.Equal(HttpStatusCode.OK, (await SendWithKeyAsync(HttpMethod.Get, "/admin/help", withHelp, null)).StatusCode);
        var put = await SendWithKeyAsync(HttpMethod.Put, $"/admin/help/{key}", withHelp, Body("Key title", "Key body", "[]"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var doc = await ReadJsonAsync(put))
            Assert.Equal("key:help-key", doc.RootElement.GetProperty("editedBy").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await SendWithKeyAsync(HttpMethod.Get, "/admin/help", without, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendWithKeyAsync(HttpMethod.Put, $"/admin/help/{key}", without, Body("T", "B", "[]"))).StatusCode);
        Assert.Equal("Key title", await ScalarAsync<string>($"select title from help_topic where key = '{key}';"));
    }

    // ---------- helpers ----------

    private async Task<HelpTopics.EnsureResult> EnsureAsync(HelpTopicSeed seed)
    {
        await using var conn = new NpgsqlConnection(_fixture.MigrateConnectionString);
        await conn.OpenAsync();
        return await new HelpTopics(seed).EnsureWrittenAsync(conn);
    }

    // The seed as JSON nodes, changed by `edit`, parsed back through the loader.
    private static HelpTopicSeed Rewrite(HelpTopicSeed seed, Action<JsonArray> edit)
    {
        var nodes = JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "help", "topics.json")))!.AsArray();
        Assert.Equal(seed.Entries.Count, nodes.Count);
        edit(nodes);
        return HelpTopicSeed.Parse(Encoding.UTF8.GetBytes(nodes.ToJsonString()));
    }

    private static string Body(string title, string body, string linksJson) =>
        "{\"title\":" + JsonSerializer.Serialize(title) + ",\"body\":" + JsonSerializer.Serialize(body) + ",\"links\":" + linksJson + "}";

    private async Task<JsonElement> GetTopicAsync(string key)
    {
        using var doc = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/admin/help", null, DevStaticTokens.EditorToken));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("key").GetString() == key).Clone();
    }

    private async Task<string> MintAsync(string name, string capabilitiesJson)
    {
        var response = await SendAsync(HttpMethod.Post, "/admin/api-keys",
            $"{{\"name\":\"{name}\",\"allCapabilities\":false,\"capabilities\":{capabilitiesJson}}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("key").GetString()!;
    }

    private static async Task AssertValidationFailedOnAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal(ApiErrorCodes.ValidationFailed, doc.RootElement.GetProperty("code").GetString());
        Assert.True(doc.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty(field, out _),
            $"expected a validation failure on {field}: {doc.RootElement.GetRawText()}");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, string? token = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token ?? DevStaticTokens.AdminToken);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host!.Client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> SendWithKeyAsync(HttpMethod method, string path, string apiKey, string? body)
    {
        using var req = _host!.KeyRequest(method, path, apiKey);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }
}
