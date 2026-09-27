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

// A51: a media asset's dark mode version (darkMediaId) and invertInDark switch.
// PATCH sets and clears the dark version and refuses the asset itself, a
// missing id, and a pending asset; invertInDark round trips; the usage and the
// delete impact list "dark version of <filename>"; deleting the dark asset
// unlinks it; the orphan collector counts a dark version as referenced.
public sealed class A51MediaDarkVersionTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A15Host? _host;

    public A51MediaDarkVersionTests(PostgresFixture fixture)
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
    public async Task Patch_sets_and_clears_the_dark_version()
    {
        var light = await UploadAndConfirmAsync("light.png", BuildPng(200, 200));
        var dark = await UploadAndConfirmAsync("dark.png", BuildPng(200, 200));

        var fresh = await GetMediaAsync(light);
        Assert.Equal(JsonValueKind.Null, fresh.RootElement.GetProperty("darkMediaId").ValueKind);
        Assert.False(fresh.RootElement.GetProperty("invertInDark").GetBoolean());

        var set = await PatchAsync(light, $"{{\"darkMediaId\":\"{dark}\"}}");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        using (var body = await ReadJsonAsync(set))
        {
            Assert.Equal(dark, body.RootElement.GetProperty("darkMediaId").GetString());
        }
        Assert.Equal(dark, (await GetMediaAsync(light)).RootElement.GetProperty("darkMediaId").GetString());

        // A PATCH without darkMediaId leaves it as it is.
        var alt = await PatchAsync(light, "{\"alt\":\"light logo\"}");
        Assert.Equal(HttpStatusCode.OK, alt.StatusCode);
        using (var body = await ReadJsonAsync(alt))
        {
            Assert.Equal(dark, body.RootElement.GetProperty("darkMediaId").GetString());
        }

        var clear = await PatchAsync(light, "{\"darkMediaId\":null}");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using (var body = await ReadJsonAsync(clear))
        {
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("darkMediaId").ValueKind);
        }
        Assert.Equal(JsonValueKind.Null,
            (await GetMediaAsync(light)).RootElement.GetProperty("darkMediaId").ValueKind);
    }

    [Fact]
    public async Task Patch_refuses_self_missing_and_pending_dark_versions()
    {
        var light = await UploadAndConfirmAsync("light.png", BuildPng(200, 200));

        var self = await PatchAsync(light, $"{{\"darkMediaId\":\"{light}\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(self));

        var missing = await PatchAsync(light, $"{{\"darkMediaId\":\"{Guid.NewGuid()}\"}}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(ApiErrorCodes.NotFound, await ReadCodeAsync(missing));

        var ticket = await GetTicketAsync("pending.png", 100);
        var pending = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        var notReady = await PatchAsync(light, $"{{\"darkMediaId\":\"{pending}\"}}");
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        Assert.Equal("media_not_ready", await ReadCodeAsync(notReady));

        // None of the refusals changed the row.
        Assert.Equal(JsonValueKind.Null,
            (await GetMediaAsync(light)).RootElement.GetProperty("darkMediaId").ValueKind);
    }

    [Fact]
    public async Task Patch_invert_in_dark_round_trips()
    {
        var id = await UploadAndConfirmAsync("icon.png", BuildPng(64, 64));

        var on = await PatchAsync(id, "{\"invertInDark\":true}");
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        using (var body = await ReadJsonAsync(on))
        {
            Assert.True(body.RootElement.GetProperty("invertInDark").GetBoolean());
        }
        Assert.True((await GetMediaAsync(id)).RootElement.GetProperty("invertInDark").GetBoolean());

        var off = await PatchAsync(id, "{\"invertInDark\":false}");
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await GetMediaAsync(id)).RootElement.GetProperty("invertInDark").GetBoolean());
    }

    [Fact]
    public async Task Usage_lists_the_assets_it_is_the_dark_version_of()
    {
        var light = await UploadAndConfirmAsync("light-logo.png", BuildPng(200, 200));
        var dark = await UploadAndConfirmAsync("dark-logo.png", BuildPng(200, 200));
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(light, $"{{\"darkMediaId\":\"{dark}\"}}")).StatusCode);

        var response = await EditorSendAsync(HttpMethod.Get, $"/admin/media/{dark}/usage", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var darkOf = body.RootElement.GetProperty("darkVersionOf").EnumerateArray().ToArray();
        var only = Assert.Single(darkOf);
        Assert.Equal(light, only.GetProperty("id").GetString());
        Assert.Equal("light-logo.png", only.GetProperty("filename").GetString());

        // The light asset is not anyone's dark version.
        var lightUsage = await EditorSendAsync(HttpMethod.Get, $"/admin/media/{light}/usage", content: null);
        using var lightBody = await ReadJsonAsync(lightUsage);
        Assert.Empty(lightBody.RootElement.GetProperty("darkVersionOf").EnumerateArray());
    }

    [Fact]
    public async Task Deleting_the_dark_asset_unlinks_it_and_the_impact_says_so()
    {
        var light = await UploadAndConfirmAsync("light-logo.png", BuildPng(200, 200));
        var dark = await UploadAndConfirmAsync("dark-logo.png", BuildPng(200, 200));
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(light, $"{{\"darkMediaId\":\"{dark}\"}}")).StatusCode);

        var preview = await EditorSendAsync(HttpMethod.Get, $"/admin/media/{dark}/impact", content: null);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using (var impact = await ReadJsonAsync(preview))
        {
            var group = Assert.Single(impact.RootElement.GetProperty("unlinks").EnumerateArray(),
                g => g.GetProperty("entity").GetString() == "media_asset");
            Assert.Equal(1, group.GetProperty("count").GetInt32());
            Assert.Equal(new[] { "dark version of light-logo.png" },
                group.GetProperty("names").EnumerateArray().Select(n => n.GetString()).ToArray());
        }

        var delete = await EditorSendAsync(HttpMethod.Delete, $"/admin/media/{dark}", content: null);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var after = await GetMediaAsync(light);
        Assert.Equal("ready", after.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, after.RootElement.GetProperty("darkMediaId").ValueKind);
    }

    [Fact]
    public async Task Orphan_collector_leaves_a_dark_version_only_asset_alone()
    {
        var light = await UploadAndConfirmAsync("light-logo.png", BuildPng(200, 200));
        var dark = await UploadAndConfirmAsync("dark-logo.png", BuildPng(200, 200));
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(light, $"{{\"darkMediaId\":\"{dark}\"}}")).StatusCode);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        // The dark asset looks unreferenced for longer than the 30 day window.
        await using (var rewind = new NpgsqlCommand(
            "update media_asset set unreferenced_since = now() - interval '31 days' where id = $1;", conn))
        {
            rewind.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(dark) });
            await rewind.ExecuteNonQueryAsync();
        }

        var collector = new MediaOrphanCollector(
            TestConnections.For(_fixture.ConnectionString), _host!.Store,
            NullLogger<MediaOrphanCollector>.Instance);
        await collector.RunOnceAsync(CancellationToken.None);

        var (darkState, darkUnref) = await ReadOrphanColumnsAsync(conn, dark);
        Assert.Equal("ready", darkState);
        Assert.Null(darkUnref);

        // The light asset is referenced by nothing, so it is stamped as usual.
        var (lightState, lightUnref) = await ReadOrphanColumnsAsync(conn, light);
        Assert.Equal("ready", lightState);
        Assert.NotNull(lightUnref);
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
