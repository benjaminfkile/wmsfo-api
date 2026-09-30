using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Wmsfo.Api.Content;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.IntegrationTests;

// A page's icon. POST and PATCH /admin/pages set it from an Icon, clear it with
// null, and leave it when absent; role pages take icons too; a malformed icon
// is 400 on the field. The content document's page entries carry it after a
// publish, the snapshot embeds that document, and a media icon joins the
// media map.
public sealed class A70PageIconTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A14Host? _host;

    public A70PageIconTests(PostgresFixture fixture)
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
        {
            await db.Database.MigrateAsync();
        }
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (var sql in new[]
            {
                "delete from snapshot;",
                "delete from content_version;",
                "delete from preview_token;",
                "delete from page;",
                "delete from sponsor_year;",
                "delete from sponsor;",
                "delete from cookie_type;",
                "delete from event;",
                "delete from media_asset;",
                "update site_setting_draft set data = '{}'::jsonb where id = 1;",
            })
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        _host = await A14Host.StartAsync(_fixture.ConnectionString);

        var starter = _host.GetService<StarterContent>();
        await using var seed = new NpgsqlConnection(_fixture.ConnectionString);
        await seed.OpenAsync();
        await starter.EnsureSeededAsync(seed, default);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Icon_sets_on_create_and_patch_clears_and_is_left_when_absent()
    {
        var created = await SendAsync(HttpMethod.Post, "/admin/pages",
            "{\"slug\":\"icon-page\",\"title\":\"Icon page\",\"navLabel\":\"Icons\",\"icon\":{\"source\":\"library\",\"id\":\"star\"}}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        long id;
        using (var body = await ReadJsonAsync(created))
        {
            id = body.RootElement.GetProperty("id").GetInt64();
            AssertIcon(body.RootElement.GetProperty("icon"), "library", "star");
        }
        using (var detail = await GetJsonAsync($"/admin/pages/{id}"))
        {
            AssertIcon(detail.RootElement.GetProperty("icon"), "library", "star");
        }

        // A create without icon stores null.
        var plain = await SendAsync(HttpMethod.Post, "/admin/pages",
            "{\"slug\":\"plain-page\",\"title\":\"Plain page\"}");
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        using (var body = await ReadJsonAsync(plain))
        {
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("icon").ValueKind);
        }

        // PATCH sets another icon, with a display.
        var patched = await SendAsync(HttpMethod.Patch, $"/admin/pages/{id}",
            "{\"icon\":{\"source\":\"library\",\"id\":\"sleigh\",\"display\":{\"shape\":\"circle\"}}}");
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        using (var body = await ReadJsonAsync(patched))
        {
            var icon = body.RootElement.GetProperty("icon");
            AssertIcon(icon, "library", "sleigh");
            Assert.Equal("circle", icon.GetProperty("display").GetProperty("shape").GetString());
        }

        // A PATCH without icon leaves it.
        var retitled = await SendAsync(HttpMethod.Patch, $"/admin/pages/{id}", "{\"title\":\"Renamed\"}");
        Assert.Equal(HttpStatusCode.OK, retitled.StatusCode);
        using (var body = await ReadJsonAsync(retitled))
        {
            AssertIcon(body.RootElement.GetProperty("icon"), "library", "sleigh");
        }

        // The list carries it.
        using (var list = await GetJsonAsync("/admin/pages"))
        {
            var item = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray(),
                p => p.GetProperty("id").GetInt64() == id);
            AssertIcon(item.GetProperty("icon"), "library", "sleigh");
        }

        // null clears it.
        var cleared = await SendAsync(HttpMethod.Patch, $"/admin/pages/{id}", "{\"icon\":null}");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using (var body = await ReadJsonAsync(cleared))
        {
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("icon").ValueKind);
        }
        using (var detail = await GetJsonAsync($"/admin/pages/{id}"))
        {
            Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("icon").ValueKind);
        }
    }

    [Fact]
    public async Task Role_pages_take_icons()
    {
        var roleId = await FindRolePageIdAsync("no_event");
        var patched = await SendAsync(HttpMethod.Patch, $"/admin/pages/{roleId}",
            "{\"icon\":{\"source\":\"library\",\"id\":\"snowflake\"}}");
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        using var body = await ReadJsonAsync(patched);
        AssertIcon(body.RootElement.GetProperty("icon"), "library", "snowflake");
        Assert.Equal("no_event", body.RootElement.GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("{\"source\":\"emoji\",\"id\":\"star\"}", "icon")]
    [InlineData("{\"source\":\"library\"}", "icon")]
    [InlineData("{\"source\":\"library\",\"id\":\"star\",\"extra\":1}", "icon")]
    [InlineData("{\"source\":\"library\",\"id\":\"star\",\"display\":{\"shape\":\"blob\"}}", "icon")]
    [InlineData("{\"source\":\"media\",\"id\":\"not-a-uuid\"}", "icon")]
    [InlineData("\"star\"", "icon")]
    [InlineData("{\"source\":\"library\",\"id\":\"no-such-icon\"}", "icon.id")]
    public async Task A_malformed_icon_is_400_on_the_field(string icon, string field)
    {
        var created = await SendAsync(HttpMethod.Post, "/admin/pages",
            $"{{\"slug\":\"bad-icon\",\"title\":\"Bad icon\",\"icon\":{icon}}}");
        await AssertFieldRefusedAsync(created, field);

        var id = await FindRolePageIdAsync("planned");
        var patched = await SendAsync(HttpMethod.Patch, $"/admin/pages/{id}", $"{{\"icon\":{icon}}}");
        await AssertFieldRefusedAsync(patched, field);

        // Neither write changed anything.
        using var detail = await GetJsonAsync($"/admin/pages/{id}");
        Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("icon").ValueKind);
        using var list = await GetJsonAsync("/admin/pages");
        Assert.DoesNotContain(list.RootElement.GetProperty("items").EnumerateArray(),
            p => p.GetProperty("slug").GetString() == "bad-icon");
    }

    [Fact]
    public async Task A_media_icon_must_name_an_existing_asset()
    {
        var id = await FindRolePageIdAsync("planned");
        var missing = await SendAsync(HttpMethod.Patch, $"/admin/pages/{id}",
            $"{{\"icon\":{{\"source\":\"media\",\"id\":\"{Guid.NewGuid()}\"}}}}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Icons_round_trip_into_the_content_document_the_snapshot_and_the_media_map()
    {
        await BootstrapFirstBootAsync();
        var media = await UploadAndConfirmPngAsync("page-icon.png", 256, 256);

        var created = await SendAsync(HttpMethod.Post, "/admin/pages",
            "{\"slug\":\"icon-page\",\"title\":\"Icon page\",\"navLabel\":\"Icons\",\"icon\":{\"source\":\"library\",\"id\":\"star\"}}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        long libraryPageId;
        using (var body = await ReadJsonAsync(created)) libraryPageId = body.RootElement.GetProperty("id").GetInt64();

        var roleId = await FindRolePageIdAsync("no_event");
        var patched = await SendAsync(HttpMethod.Patch, $"/admin/pages/{roleId}",
            $"{{\"icon\":{{\"source\":\"media\",\"id\":\"{media}\"}}}}");
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var publish = await SendAsync(HttpMethod.Post, "/admin/content/publish", "{}");
        Assert.Equal(HttpStatusCode.Created, publish.StatusCode);

        // The published content document.
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "select document::text, media_ids from content_version order by id desc limit 1;", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            using var document = JsonDocument.Parse(reader.GetString(0));
            AssertPageIcons(document.RootElement, libraryPageId, roleId, media);
            Assert.Contains(Guid.Parse(media), reader.GetFieldValue<Guid[]>(1));
        }

        // The snapshot embeds the document and the media map carries the media icon.
        var snapshotBytes = await GetLatestSnapshotBytesAsync();
        Assert.NotNull(snapshotBytes);
        using (var snap = JsonDocument.Parse(snapshotBytes!))
        {
            AssertPageIcons(snap.RootElement.GetProperty("content"), libraryPageId, roleId, media);
            Assert.True(snap.RootElement.GetProperty("media").TryGetProperty(media, out _));
        }

        // The draft document carries the icons and the media entry as well.
        using (var draft = await GetJsonAsync("/admin/content/draft"))
        {
            Assert.True(draft.RootElement.GetProperty("media").TryGetProperty(media, out _));
        }

        // Clearing the icon and publishing again drops it from the document and the media map.
        var cleared = await SendAsync(HttpMethod.Patch, $"/admin/pages/{roleId}", "{\"icon\":null}");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var republish = await SendAsync(HttpMethod.Post, "/admin/content/publish", "{}");
        Assert.Equal(HttpStatusCode.Created, republish.StatusCode);
        using (var snap = JsonDocument.Parse((await GetLatestSnapshotBytesAsync())!))
        {
            var rolePage = FindPage(snap.RootElement.GetProperty("content"), roleId);
            Assert.Equal(JsonValueKind.Null, rolePage.GetProperty("icon").ValueKind);
            Assert.False(snap.RootElement.GetProperty("media").TryGetProperty(media, out _));
        }
    }

    [Fact]
    public async Task Deleting_the_media_clears_the_page_icon()
    {
        var media = await UploadAndConfirmPngAsync("page-icon.png", 64, 64);
        var roleId = await FindRolePageIdAsync("ended");
        var patched = await SendAsync(HttpMethod.Patch, $"/admin/pages/{roleId}",
            $"{{\"icon\":{{\"source\":\"media\",\"id\":\"{media}\"}}}}");
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        using (var usage = await GetJsonAsync($"/admin/media/{media}/usage"))
        {
            Assert.Contains(usage.RootElement.GetProperty("draftPages").EnumerateArray(),
                p => p.GetProperty("id").GetInt64() == roleId);
        }

        var deleted = await SendAsync(HttpMethod.Delete, $"/admin/media/{media}", null);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var detail = await GetJsonAsync($"/admin/pages/{roleId}");
        Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("icon").ValueKind);
    }

    // ---------- helpers ----------

    private static void AssertPageIcons(JsonElement document, long libraryPageId, long mediaPageId, string mediaId)
    {
        foreach (var page in document.GetProperty("pages").EnumerateArray())
        {
            // Every page entry carries the key.
            Assert.True(page.TryGetProperty("icon", out _));
        }
        AssertIcon(FindPage(document, libraryPageId).GetProperty("icon"), "library", "star");
        AssertIcon(FindPage(document, mediaPageId).GetProperty("icon"), "media", mediaId);
    }

    private static JsonElement FindPage(JsonElement document, long id) =>
        document.GetProperty("pages").EnumerateArray().Single(p => p.GetProperty("id").GetInt64() == id);

    private static void AssertIcon(JsonElement icon, string source, string id)
    {
        Assert.Equal(JsonValueKind.Object, icon.ValueKind);
        Assert.Equal(source, icon.GetProperty("source").GetString());
        Assert.Equal(id, icon.GetProperty("id").GetString());
    }

    private static async Task AssertFieldRefusedAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(ApiErrorCodes.ValidationFailed, body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty(field, out _),
            $"expected a problem on `{field}`: {body.RootElement.GetRawText()}");
    }

    private async Task<long> FindRolePageIdAsync(string role)
    {
        using var list = await GetJsonAsync("/admin/pages");
        return list.RootElement.GetProperty("items").EnumerateArray()
            .Single(p => p.GetProperty("role").GetString() == role)
            .GetProperty("id").GetInt64();
    }

    // Publisher.EnsureVersionOneAsync plus snapshot v1, so the publish frame has
    // a snapshot row to lock.
    private async Task BootstrapFirstBootAsync()
    {
        var publisher = _host!.GetService<Publisher>();
        var snapshotBuilder = _host.GetService<SnapshotBuilder>();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await publisher.EnsureVersionOneAsync(conn, default);
        await using var tx = await conn.BeginTransactionAsync();
        var built = await snapshotBuilder.BuildAndPutAsync(conn, tx, default);
        await using var ins = new NpgsqlCommand(@"
insert into snapshot (id, version, url, s3_key, built_at) values (1, 1, $1, $2, now());", conn, tx);
        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = built.Url });
        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = built.Key });
        await ins.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    private async Task<byte[]?> GetLatestSnapshotBytesAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select s3_key from snapshot where id = 1;", conn);
        var key = (string?)await cmd.ExecuteScalarAsync();
        if (key is null) return null;
        var obj = await _host!.Store.GetObjectAsync(key);
        return obj?.Bytes;
    }

    private async Task<string> UploadAndConfirmPngAsync(string filename, int width, int height)
    {
        byte[] bytes;
        using (var image = new Image<Rgba32>(width, height, new Rgba32(200, 0, 0, 255)))
        using (var ms = new MemoryStream())
        {
            image.Save(ms, new PngEncoder());
            bytes = ms.ToArray();
        }
        var ticketResp = await SendAsync(HttpMethod.Post, "/admin/media/upload-url",
            $"{{\"filename\":\"{filename}\",\"contentType\":\"image/png\",\"sizeBytes\":{bytes.LongLength},\"alt\":\"\",\"title\":\"\"}}");
        Assert.Equal(HttpStatusCode.Created, ticketResp.StatusCode);
        using var ticket = await ReadJsonAsync(ticketResp);
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        var uri = new Uri(ticket.RootElement.GetProperty("uploadUrl").GetString()!);
        using var put = new HttpRequestMessage(HttpMethod.Put, uri.PathAndQuery) { Content = new ByteArrayContent(bytes) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        put.Headers.Add("x-amz-tagging", "state=pending");
        Assert.Equal(HttpStatusCode.NoContent, (await _host!.Client.SendAsync(put)).StatusCode);
        var confirm = await SendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        return id;
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        var response = await SendAsync(HttpMethod.Get, path, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json)
    {
        using var req = _host!.EditorRequest(method, path);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());
}
