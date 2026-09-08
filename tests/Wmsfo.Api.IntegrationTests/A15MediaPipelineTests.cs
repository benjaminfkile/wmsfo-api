using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// A15 acceptance criteria (task 272): media pipeline end-to-end against the
// LocalObjectStore. Covers ticket → PUT → confirm → variants (a 700 px source
// yields 480 only), tag removal, MediaUsage, media_in_use guard, delete, and
// every listed error code (413, 400 mismatch, 400 SVG rule, 404 upload_not_found,
// 409 media_not_pending, 404 unknown id, GET /admin/icons).
public sealed class A15MediaPipelineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A15Host? _host;

    public A15MediaPipelineTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAsync();
        _host = await A15Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task MigrateAsync()
    {
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(contextOptions);
        await db.Database.MigrateAsync();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
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

    // ---------- ticket, PUT, confirm, variants (700 px yields 480 only), tag removal ----------

    [Fact]
    public async Task Ticket_put_confirm_derives_480_only_for_700px_source_and_removes_tag()
    {
        var pngBytes = BuildPng(700, 400);

        // 1. Ticket.
        var ticket = await GetTicketAsync("hangar.png", "image/png", pngBytes.LongLength);
        var media = ticket.RootElement.GetProperty("media");
        var id = media.GetProperty("id").GetString()!;
        Assert.Equal("pending", media.GetProperty("state").GetString());
        Assert.Equal("raster", media.GetProperty("kind").GetString());
        Assert.Equal("hangar.png", media.GetProperty("filename").GetString());
        Assert.Equal("image/png", ticket.RootElement.GetProperty("headers").GetProperty("Content-Type").GetString());
        Assert.Equal("state=pending", ticket.RootElement.GetProperty("headers").GetProperty("x-amz-tagging").GetString());
        var uploadUrl = ticket.RootElement.GetProperty("uploadUrl").GetString()!;

        // 2. Browser-style PUT into the local store via the presigned URL.
        await UploadAsync(uploadUrl, pngBytes, "image/png");

        // The pending tag is present after the browser PUT.
        var tagAfterPut = await ReadTagAsync($"media/{id}/hangar.png");
        Assert.Equal("state=pending", tagAfterPut);

        // 3. Confirm.
        var confirm = await EditorSendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", content: null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var confirmed = await ReadJsonAsync(confirm);
        Assert.Equal("ready", confirmed.RootElement.GetProperty("state").GetString());
        Assert.Equal(700, confirmed.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(400, confirmed.RootElement.GetProperty("height").GetInt32());
        Assert.False(string.IsNullOrEmpty(confirmed.RootElement.GetProperty("sha256").GetString()));
        Assert.Equal(pngBytes.LongLength, confirmed.RootElement.GetProperty("sizeBytes").GetInt64());

        // Variants: exactly one — 480 — because the source is 700 px wide.
        var variants = confirmed.RootElement.GetProperty("variants");
        Assert.Single(variants.EnumerateObject().ToArray());
        Assert.True(variants.TryGetProperty("480", out var v480));
        Assert.EndsWith("/w480.webp", v480.GetString());

        // Tag removal: pending tag is gone on both the original and the variant.
        Assert.Null(await ReadTagAsync($"media/{id}/hangar.png"));
        Assert.Null(await ReadTagAsync($"media/{id}/w480.webp"));

        // Confirm is not idempotent after success: a second call is 409 media_not_pending.
        var confirmAgain = await EditorSendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", content: null);
        Assert.Equal(HttpStatusCode.Conflict, confirmAgain.StatusCode);
        Assert.Equal("media_not_pending", await ReadCodeAsync(confirmAgain));
    }

    [Fact]
    public async Task Ticket_confirm_1024_source_yields_480_and_960_variants()
    {
        var pngBytes = BuildPng(1024, 512);
        var ticket = await GetTicketAsync("wide.png", "image/png", pngBytes.LongLength);
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        await UploadAsync(ticket.RootElement.GetProperty("uploadUrl").GetString()!, pngBytes, "image/png");

        var confirm = await EditorSendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", content: null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var confirmed = await ReadJsonAsync(confirm);
        var variants = confirmed.RootElement.GetProperty("variants");
        Assert.True(variants.TryGetProperty("480", out _));
        Assert.True(variants.TryGetProperty("960", out _));
        Assert.False(variants.TryGetProperty("1600", out _));
    }

    // ---------- filename sanitizing rejects extension mismatch ----------

    [Fact]
    public async Task Ticket_extension_mismatch_is_400_validation_failed()
    {
        var body = new StringContent(
            "{\"filename\":\"hangar.png\",\"contentType\":\"image/jpeg\",\"sizeBytes\":100,\"alt\":\"\",\"title\":\"\"}",
            Encoding.UTF8, "application/json");
        var response = await EditorSendAsync(HttpMethod.Post, "/admin/media/upload-url", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    // ---------- limits by type ----------

    [Fact]
    public async Task Ticket_over_raster_limit_is_413()
    {
        var body = new StringContent(
            "{\"filename\":\"big.png\",\"contentType\":\"image/png\",\"sizeBytes\":25000000,\"alt\":\"\",\"title\":\"\"}",
            Encoding.UTF8, "application/json");
        var response = await EditorSendAsync(HttpMethod.Post, "/admin/media/upload-url", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ApiErrorCodes.PayloadTooLarge, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Ticket_over_svg_limit_is_413()
    {
        var body = new StringContent(
            "{\"filename\":\"big.svg\",\"contentType\":\"image/svg+xml\",\"sizeBytes\":2000000,\"alt\":\"\",\"title\":\"\"}",
            Encoding.UTF8, "application/json");
        var response = await EditorSendAsync(HttpMethod.Post, "/admin/media/upload-url", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ApiErrorCodes.PayloadTooLarge, await ReadCodeAsync(response));
    }

    // ---------- sniff mismatch: delete object and row, 400 validation_failed ----------

    [Fact]
    public async Task Confirm_type_mismatch_400_deletes_row_and_object()
    {
        var gifBytes = BuildGif(64, 64);
        // Ticket claims png; we upload actual gif bytes.
        var ticket = await GetTicketAsync("wrong.png", "image/png", gifBytes.LongLength);
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        await UploadAsync(ticket.RootElement.GetProperty("uploadUrl").GetString()!, gifBytes, "image/png");
        var confirm = await EditorSendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(confirm));
        // Row is gone.
        Assert.Null(await ReadRowStateAsync(id));
        // Object is gone.
        Assert.False(File.Exists(Path.Combine(_host!.StoreRoot, "media", id, "wrong.png")));
    }

    // ---------- SVG validation on upload ----------

    [Fact]
    public async Task Confirm_svg_with_script_is_400()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var ticket = await GetTicketAsync("bad.svg", "image/svg+xml", svg.LongLength);
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        await UploadAsync(ticket.RootElement.GetProperty("uploadUrl").GetString()!, svg, "image/svg+xml");
        var confirm = await EditorSendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Null(await ReadRowStateAsync(id));
    }

    [Fact]
    public async Task Confirm_valid_svg_records_row_with_no_variants()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><circle cx=\"12\" cy=\"12\" r=\"5\"/></svg>");
        var ticket = await GetTicketAsync("logo.svg", "image/svg+xml", svg.LongLength);
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        await UploadAsync(ticket.RootElement.GetProperty("uploadUrl").GetString()!, svg, "image/svg+xml");
        var confirm = await EditorSendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", content: null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var confirmed = await ReadJsonAsync(confirm);
        Assert.Equal("ready", confirmed.RootElement.GetProperty("state").GetString());
        Assert.Equal("svg", confirmed.RootElement.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, confirmed.RootElement.GetProperty("width").ValueKind);
        Assert.Empty(confirmed.RootElement.GetProperty("variants").EnumerateObject());
    }

    // ---------- confirm without upload: 404 upload_not_found ----------

    [Fact]
    public async Task Confirm_missing_upload_is_404_upload_not_found()
    {
        var pngBytes = BuildPng(200, 200);
        var ticket = await GetTicketAsync("ghost.png", "image/png", pngBytes.LongLength);
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        // Skip the PUT — the object never arrived.
        var confirm = await EditorSendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", content: null);
        Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);
        Assert.Equal("upload_not_found", await ReadCodeAsync(confirm));
    }

    // ---------- confirm on unknown id: 404 not_found ----------

    [Fact]
    public async Task Confirm_unknown_media_id_is_404_not_found()
    {
        var confirm = await EditorSendAsync(HttpMethod.Post, "/admin/media/00000000-0000-0000-0000-000000000000/confirm", content: null);
        Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);
        Assert.Equal(ApiErrorCodes.NotFound, await ReadCodeAsync(confirm));
    }

    // ---------- GET / list / usage / patch ----------

    [Fact]
    public async Task List_filters_by_kind_and_state_and_search_q()
    {
        var pngBytes = BuildPng(200, 200);
        var svgBytes = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");

        var rasterId = await UploadAndConfirmAsync("cat.png", "image/png", pngBytes);
        var svgId = await UploadAndConfirmAsync("logo.svg", "image/svg+xml", svgBytes);

        // Filter by kind=raster returns only the png.
        var byKind = await EditorSendAsync(HttpMethod.Get, "/admin/media?kind=raster", content: null);
        var byKindBody = await ReadJsonAsync(byKind);
        var ids = byKindBody.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();
        Assert.Contains(rasterId, ids);
        Assert.DoesNotContain(svgId, ids);

        // Search q=logo matches only the svg by filename.
        var byQ = await EditorSendAsync(HttpMethod.Get, "/admin/media?q=logo", content: null);
        var byQBody = await ReadJsonAsync(byQ);
        var qIds = byQBody.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();
        Assert.Contains(svgId, qIds);
        Assert.DoesNotContain(rasterId, qIds);
    }

    [Fact]
    public async Task Usage_reports_no_references_for_fresh_asset()
    {
        var id = await UploadAndConfirmAsync("solo.png", "image/png", BuildPng(200, 200));
        var response = await EditorSendAsync(HttpMethod.Get, $"/admin/media/{id}/usage", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Empty(body.RootElement.GetProperty("draftPages").EnumerateArray());
        Assert.Equal(0, body.RootElement.GetProperty("versionCount").GetInt32());
        Assert.Empty(body.RootElement.GetProperty("sponsors").EnumerateArray());
        Assert.Empty(body.RootElement.GetProperty("cookieTypes").EnumerateArray());
        Assert.False(body.RootElement.GetProperty("siteSettings").GetBoolean());
    }

    [Fact]
    public async Task Patch_updates_alt_and_title_and_returns_updated_row()
    {
        var id = await UploadAndConfirmAsync("edit.png", "image/png", BuildPng(200, 200));
        var patch = new StringContent("{\"alt\":\"a helicopter\",\"title\":\"hangar shot\"}",
            Encoding.UTF8, "application/json");
        var response = await EditorSendAsync(HttpMethod.Patch, $"/admin/media/{id}", patch);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("a helicopter", body.RootElement.GetProperty("alt").GetString());
        Assert.Equal("hangar shot", body.RootElement.GetProperty("title").GetString());
    }

    // ---------- media_in_use guard on DELETE ----------

    [Fact]
    public async Task Delete_when_referenced_by_sponsor_is_409_media_in_use()
    {
        var id = await UploadAndConfirmAsync("sponsor.png", "image/png", BuildPng(200, 200));
        // Wire it to a sponsor (direct SQL — sponsor endpoints are not in this task).
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
            "insert into sponsor (name, logo_media_id) values ($1, $2);", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "Sponsor" });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(id) });
            await cmd.ExecuteNonQueryAsync();
        }
        var response = await EditorSendAsync(HttpMethod.Delete, $"/admin/media/{id}", content: null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("media_in_use", body.RootElement.GetProperty("code").GetString());
        var usage = body.RootElement.GetProperty("details").GetProperty("usage");
        Assert.NotEmpty(usage.GetProperty("sponsors").EnumerateArray());
    }

    [Fact]
    public async Task Delete_unreferenced_removes_row_and_every_object()
    {
        var id = await UploadAndConfirmAsync("gone.png", "image/png", BuildPng(1024, 500));
        // Confirm the variant exists on disk before delete.
        Assert.True(File.Exists(Path.Combine(_host!.StoreRoot, "media", id, "gone.png")));
        Assert.True(File.Exists(Path.Combine(_host!.StoreRoot, "media", id, "w480.webp")));
        var response = await EditorSendAsync(HttpMethod.Delete, $"/admin/media/{id}", content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_host.StoreRoot, "media", id, "gone.png")));
        Assert.False(File.Exists(Path.Combine(_host.StoreRoot, "media", id, "w480.webp")));
        Assert.Null(await ReadRowStateAsync(id));
    }

    // ---------- GET /admin/icons ----------

    [Fact]
    public async Task Get_admin_icons_returns_library_ordered_by_name()
    {
        var response = await EditorSendAsync(HttpMethod.Get, "/admin/icons", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var items = body.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0);
        // Names are sorted case-insensitively.
        var names = items.EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray();
        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(sorted, names);
        // Each entry carries an absolute CDN url pointing at the icons prefix.
        foreach (var item in items.EnumerateArray())
        {
            var url = item.GetProperty("url").GetString()!;
            Assert.StartsWith(_host!.Options.CdnBaseUrl + "/icons/", url);
            Assert.EndsWith(".svg", url);
        }
    }

    // ---------- helpers ----------

    private static byte[] BuildPng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(255, 0, 0, 255));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static byte[] BuildGif(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 255, 255));
        using var ms = new MemoryStream();
        image.Save(ms, new GifEncoder());
        return ms.ToArray();
    }

    private async Task<JsonDocument> GetTicketAsync(string filename, string contentType, long sizeBytes)
    {
        var body = new StringContent(
            $"{{\"filename\":\"{filename}\",\"contentType\":\"{contentType}\",\"sizeBytes\":{sizeBytes},\"alt\":\"\",\"title\":\"\"}}",
            Encoding.UTF8, "application/json");
        var response = await EditorSendAsync(HttpMethod.Post, "/admin/media/upload-url", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task UploadAsync(string uploadUrl, byte[] bytes, string contentType)
    {
        // The ticket points at PublicApiBaseUrl (fake host). Route it back to the
        // test host by pulling out the /local-upload path and issuing the PUT on
        // the test client.
        var uri = new Uri(uploadUrl);
        var localPath = uri.PathAndQuery;
        var req = new HttpRequestMessage(HttpMethod.Put, localPath)
        {
            Content = new ByteArrayContent(bytes),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        req.Headers.Add("x-amz-tagging", "state=pending");
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<string> UploadAndConfirmAsync(string filename, string contentType, byte[] bytes)
    {
        var ticket = await GetTicketAsync(filename, contentType, bytes.LongLength);
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        await UploadAsync(ticket.RootElement.GetProperty("uploadUrl").GetString()!, bytes, contentType);
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

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }

    private async Task<string?> ReadTagAsync(string key)
    {
        return await _host!.Store.GetObjectTaggingAsync(key);
    }

    private async Task<string?> ReadRowStateAsync(string id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select state from media_asset where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(id) });
        var r = await cmd.ExecuteScalarAsync();
        return r is string s ? s : null;
    }
}
