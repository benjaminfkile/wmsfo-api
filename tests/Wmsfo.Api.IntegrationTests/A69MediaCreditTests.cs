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
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// A media asset's credit. PATCH sets it (trimmed), clears it with null, leaves
// it when absent, and refuses a blank credit or one longer than 200
// characters; the admin object carries it.
public sealed class A69MediaCreditTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A15Host? _host;

    public A69MediaCreditTests(PostgresFixture fixture)
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
    public async Task Patch_sets_clears_and_round_trips_the_credit()
    {
        var photo = await UploadAndConfirmAsync("photo.png", BuildPng(200, 100));

        var fresh = await GetMediaAsync(photo);
        Assert.Equal(JsonValueKind.Null, fresh.RootElement.GetProperty("credit").ValueKind);

        var set = await PatchAsync(photo, "{\"credit\":\"  Photo by Jane Doe  \"}");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        using (var body = await ReadJsonAsync(set))
        {
            Assert.Equal("Photo by Jane Doe", body.RootElement.GetProperty("credit").GetString());
        }
        Assert.Equal("Photo by Jane Doe", (await GetMediaAsync(photo)).RootElement.GetProperty("credit").GetString());

        // The list carries the credit too.
        var list = await EditorSendAsync(HttpMethod.Get, "/admin/media", content: null);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var page = await ReadJsonAsync(list))
        {
            var item = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray(),
                i => i.GetProperty("id").GetString() == photo);
            Assert.Equal("Photo by Jane Doe", item.GetProperty("credit").GetString());
        }

        // A PATCH without credit leaves it as it is.
        var alt = await PatchAsync(photo, "{\"alt\":\"a photo\"}");
        Assert.Equal(HttpStatusCode.OK, alt.StatusCode);
        using (var body = await ReadJsonAsync(alt))
        {
            Assert.Equal("Photo by Jane Doe", body.RootElement.GetProperty("credit").GetString());
        }

        var exact = new string('c', 200);
        var longest = await PatchAsync(photo, $"{{\"credit\":\"{exact}\"}}");
        Assert.Equal(HttpStatusCode.OK, longest.StatusCode);
        Assert.Equal(exact, (await GetMediaAsync(photo)).RootElement.GetProperty("credit").GetString());

        var clear = await PatchAsync(photo, "{\"credit\":null}");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using (var body = await ReadJsonAsync(clear))
        {
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("credit").ValueKind);
        }
        Assert.Equal(JsonValueKind.Null,
            (await GetMediaAsync(photo)).RootElement.GetProperty("credit").ValueKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Patch_refuses_a_blank_or_too_long_credit(string? credit)
    {
        var photo = await UploadAndConfirmAsync("photo.png", BuildPng(200, 100));
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(photo, "{\"credit\":\"Jane Doe\"}")).StatusCode);

        // null stands for 201 characters here.
        var value = credit ?? new string('c', 201);
        var refused = await PatchAsync(photo, $"{{\"credit\":\"{value}\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using (var body = await ReadJsonAsync(refused))
        {
            Assert.Equal(ApiErrorCodes.ValidationFailed, body.RootElement.GetProperty("code").GetString());
            Assert.True(body.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty("credit", out _));
        }

        // The refusal left the row as it was.
        Assert.Equal("Jane Doe", (await GetMediaAsync(photo)).RootElement.GetProperty("credit").GetString());
    }

    // ---------- helpers ----------

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
