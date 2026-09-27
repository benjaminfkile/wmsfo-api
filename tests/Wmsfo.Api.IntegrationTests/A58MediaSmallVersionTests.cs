using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Chores;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// A media asset's small screen version (smallMediaId). PATCH sets and clears
// it and refuses the asset itself, a missing id, and a pending asset; the
// delete impact lists "small version of <filename>" and deleting the small
// asset unlinks it; the orphan collector counts a small version as referenced.
public sealed class A58MediaSmallVersionTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A15Host? _host;

    public A58MediaSmallVersionTests(PostgresFixture fixture)
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
                "delete from content_version;",
                "delete from cookie_type;",
                "delete from sponsor_year;",
                "delete from sponsor;",
                "delete from section_item;",
                "delete from section;",
                "delete from page;",
                "update site_setting_draft set data = '{}'::jsonb where id = 1;",
                "delete from media_asset;",
            })
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        _host = await A15Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Patch_sets_and_clears_the_small_version()
    {
        var wide = await UploadAndConfirmAsync("wide.png", BuildPng(200, 100));
        var small = await UploadAndConfirmAsync("small.png", BuildPng(100, 100));

        var fresh = await GetMediaAsync(wide);
        Assert.Equal(JsonValueKind.Null, fresh.RootElement.GetProperty("smallMediaId").ValueKind);

        var set = await PatchAsync(wide, $"{{\"smallMediaId\":\"{small}\"}}");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        using (var body = await ReadJsonAsync(set))
        {
            Assert.Equal(small, body.RootElement.GetProperty("smallMediaId").GetString());
        }
        Assert.Equal(small, (await GetMediaAsync(wide)).RootElement.GetProperty("smallMediaId").GetString());

        // A PATCH without smallMediaId leaves it as it is.
        var alt = await PatchAsync(wide, "{\"alt\":\"wide banner\"}");
        Assert.Equal(HttpStatusCode.OK, alt.StatusCode);
        using (var body = await ReadJsonAsync(alt))
        {
            Assert.Equal(small, body.RootElement.GetProperty("smallMediaId").GetString());
        }

        var clear = await PatchAsync(wide, "{\"smallMediaId\":null}");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using (var body = await ReadJsonAsync(clear))
        {
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("smallMediaId").ValueKind);
        }
        Assert.Equal(JsonValueKind.Null,
            (await GetMediaAsync(wide)).RootElement.GetProperty("smallMediaId").ValueKind);
    }

    [Fact]
    public async Task Patch_refuses_self_missing_and_pending_small_versions()
    {
        var wide = await UploadAndConfirmAsync("wide.png", BuildPng(200, 100));

        var self = await PatchAsync(wide, $"{{\"smallMediaId\":\"{wide}\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(self));

        var missing = await PatchAsync(wide, $"{{\"smallMediaId\":\"{Guid.NewGuid()}\"}}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(ApiErrorCodes.NotFound, await ReadCodeAsync(missing));

        var ticket = await GetTicketAsync("pending.png", 100);
        var pending = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        var notReady = await PatchAsync(wide, $"{{\"smallMediaId\":\"{pending}\"}}");
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        Assert.Equal("media_not_ready", await ReadCodeAsync(notReady));

        // None of the refusals changed the row.
        Assert.Equal(JsonValueKind.Null,
            (await GetMediaAsync(wide)).RootElement.GetProperty("smallMediaId").ValueKind);
    }

    [Fact]
    public async Task Deleting_the_small_asset_unlinks_it_and_the_impact_says_so()
    {
        var wide = await UploadAndConfirmAsync("wide-banner.png", BuildPng(200, 100));
        var small = await UploadAndConfirmAsync("small-banner.png", BuildPng(100, 100));
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(wide, $"{{\"smallMediaId\":\"{small}\"}}")).StatusCode);

        var preview = await EditorSendAsync(HttpMethod.Get, $"/admin/media/{small}/impact", content: null);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using (var impact = await ReadJsonAsync(preview))
        {
            var group = Assert.Single(impact.RootElement.GetProperty("unlinks").EnumerateArray(),
                g => g.GetProperty("entity").GetString() == "media_asset");
            Assert.Equal(1, group.GetProperty("count").GetInt32());
            Assert.Equal(new[] { "small version of wide-banner.png" },
                group.GetProperty("names").EnumerateArray().Select(n => n.GetString()).ToArray());
        }

        var delete = await EditorSendAsync(HttpMethod.Delete, $"/admin/media/{small}", content: null);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var after = await GetMediaAsync(wide);
        Assert.Equal("ready", after.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, after.RootElement.GetProperty("smallMediaId").ValueKind);
    }

    [Fact]
    public async Task Orphan_collector_keeps_a_referenced_small_version()
    {
        var wide = await UploadAndConfirmAsync("wide-banner.png", BuildPng(200, 100));
        var small = await UploadAndConfirmAsync("small-banner.png", BuildPng(100, 100));
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(wide, $"{{\"smallMediaId\":\"{small}\"}}")).StatusCode);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        // The small asset looks unreferenced for longer than the 30 day window.
        await using (var rewind = new NpgsqlCommand(
            "update media_asset set unreferenced_since = now() - interval '31 days' where id = $1;", conn))
        {
            rewind.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(small) });
            await rewind.ExecuteNonQueryAsync();
        }

        var collector = new MediaOrphanCollector(
            TestConnections.For(_fixture.ConnectionString), _host!.Store,
            NullLogger<MediaOrphanCollector>.Instance);
        await collector.RunOnceAsync(CancellationToken.None);

        var (smallState, smallUnref) = await ReadOrphanColumnsAsync(conn, small);
        Assert.Equal("ready", smallState);
        Assert.Null(smallUnref);

        // The wide asset is referenced by nothing, so it is stamped as usual.
        var (wideState, wideUnref) = await ReadOrphanColumnsAsync(conn, wide);
        Assert.Equal("ready", wideState);
        Assert.NotNull(wideUnref);
    }

    // ---------- helpers ----------

    private static async Task<(string? State, DateTimeOffset? UnreferencedSince)> ReadOrphanColumnsAsync(
        NpgsqlConnection conn, string id)
    {
        await using var cmd = new NpgsqlCommand(
            "select state, unreferenced_since from media_asset where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(id) });
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (null, null);
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    private static byte[] BuildPng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 128, 0, 255));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private async Task<JsonDocument> GetMediaAsync(string id)
    {
        var response = await EditorSendAsync(HttpMethod.Get, $"/admin/media/{id}", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private Task<HttpResponseMessage> PatchAsync(string id, string json) =>
        EditorSendAsync(HttpMethod.Patch, $"/admin/media/{id}",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<JsonDocument> GetTicketAsync(string filename, long sizeBytes)
    {
        var body = new StringContent(
            $"{{\"filename\":\"{filename}\",\"contentType\":\"image/png\",\"sizeBytes\":{sizeBytes},\"alt\":\"\",\"title\":\"\"}}",
            Encoding.UTF8, "application/json");
        var response = await EditorSendAsync(HttpMethod.Post, "/admin/media/upload-url", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<string> UploadAndConfirmAsync(string filename, byte[] bytes)
    {
        using var ticket = await GetTicketAsync(filename, bytes.LongLength);
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        var uri = new Uri(ticket.RootElement.GetProperty("uploadUrl").GetString()!);
        var put = new HttpRequestMessage(HttpMethod.Put, uri.PathAndQuery) { Content = new ByteArrayContent(bytes) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        put.Headers.Add("x-amz-tagging", "state=pending");
        Assert.Equal(HttpStatusCode.NoContent, (await _host!.Client.SendAsync(put)).StatusCode);
        var confirm = await EditorSendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", content: null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        return id;
    }

    private async Task<HttpResponseMessage> EditorSendAsync(HttpMethod method, string path, HttpContent? content)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.EditorToken);
        if (content is not null) req.Content = content;
        return await _host!.Client.SendAsync(req);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());

    private static async Task<string> ReadCodeAsync(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }
}
