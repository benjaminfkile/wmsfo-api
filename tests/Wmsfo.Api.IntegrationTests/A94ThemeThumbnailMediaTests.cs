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

namespace Wmsfo.Api.IntegrationTests;

// A94: a tracker theme's thumbnail is a counted media reference (sql.md 8.22,
// 9.6): MediaUsage.themes lists the theme, the delete impact lists it under
// unlinks, the delete nulls the column, and the orphan chore keeps the asset.
public sealed class A94ThemeThumbnailMediaTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A15Host? _host;

    public A94ThemeThumbnailMediaTests(PostgresFixture fixture)
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
                "update tracker_theme set thumbnail_media_id = null;",
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
    public async Task Usage_lists_the_theme_the_impact_unlinks_it_and_the_delete_nulls_the_column()
    {
        var thumb = await UploadAndConfirmAsync("night-thumb.png", BuildPng(320, 200));
        var themeId = await SetThumbnailAsync("night", thumb);

        var usage = await EditorSendAsync(HttpMethod.Get, $"/admin/media/{thumb}/usage");
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
        using (var body = await ReadJsonAsync(usage))
        {
            var theme = Assert.Single(body.RootElement.GetProperty("themes").EnumerateArray());
            Assert.Equal(themeId, theme.GetProperty("id").GetInt64());
            Assert.Equal("Night", theme.GetProperty("name").GetString());
        }

        var preview = await EditorSendAsync(HttpMethod.Get, $"/admin/media/{thumb}/impact");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using (var impact = await ReadJsonAsync(preview))
        {
            var group = Assert.Single(impact.RootElement.GetProperty("unlinks").EnumerateArray(),
                g => g.GetProperty("entity").GetString() == "theme");
            Assert.Equal(1, group.GetProperty("count").GetInt32());
            Assert.Equal(new[] { "Night" }, group.GetProperty("names").EnumerateArray().Select(n => n.GetString()).ToArray());
        }

        var delete = await EditorSendAsync(HttpMethod.Delete, $"/admin/media/{thumb}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select thumbnail_media_id from tracker_theme where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = themeId });
        Assert.Equal(DBNull.Value, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Orphan_collector_leaves_a_thumbnail_referenced_asset_ready()
    {
        var thumb = await UploadAndConfirmAsync("standard-thumb.png", BuildPng(320, 200));
        var loose = await UploadAndConfirmAsync("loose.png", BuildPng(320, 200));
        await SetThumbnailAsync("standard", thumb);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var rewind = new NpgsqlCommand(
            "update media_asset set unreferenced_since = now() - interval '31 days' where id = $1;", conn))
        {
            rewind.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(thumb) });
            await rewind.ExecuteNonQueryAsync();
        }

        var collector = new MediaOrphanCollector(
            TestConnections.For(_fixture.ConnectionString), _host!.Store,
            NullLogger<MediaOrphanCollector>.Instance);
        await collector.RunOnceAsync(CancellationToken.None);

        var (thumbState, thumbUnref) = await ReadOrphanColumnsAsync(conn, thumb);
        Assert.Equal("ready", thumbState);
        Assert.Null(thumbUnref);

        var (looseState, looseUnref) = await ReadOrphanColumnsAsync(conn, loose);
        Assert.Equal("ready", looseState);
        Assert.NotNull(looseUnref);
    }

    // ---------- helpers ----------

    private async Task<long> SetThumbnailAsync(string key, string mediaId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "update tracker_theme set thumbnail_media_id = $1 where key = $2 returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(mediaId) });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

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
        using var image = new Image<Rgba32>(width, height, new Rgba32(20, 40, 160, 255));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private async Task<string> UploadAndConfirmAsync(string filename, byte[] bytes)
    {
        var ticketBody = new StringContent(
            $"{{\"filename\":\"{filename}\",\"contentType\":\"image/png\",\"sizeBytes\":{bytes.LongLength},\"alt\":\"\",\"title\":\"\"}}",
            Encoding.UTF8, "application/json");
        var ticketResponse = await EditorSendAsync(HttpMethod.Post, "/admin/media/upload-url", ticketBody);
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        using var ticket = await ReadJsonAsync(ticketResponse);
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        var uri = new Uri(ticket.RootElement.GetProperty("uploadUrl").GetString()!);
        var put = new HttpRequestMessage(HttpMethod.Put, uri.PathAndQuery) { Content = new ByteArrayContent(bytes) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        put.Headers.Add("x-amz-tagging", "state=pending");
        Assert.Equal(HttpStatusCode.NoContent, (await _host!.Client.SendAsync(put)).StatusCode);
        var confirm = await EditorSendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm");
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        return id;
    }

    private async Task<HttpResponseMessage> EditorSendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.EditorToken);
        if (content is not null) req.Content = content;
        return await _host!.Client.SendAsync(req);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());
}
