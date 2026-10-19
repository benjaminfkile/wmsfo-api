using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Wmsfo.Api.Chores;
using Wmsfo.Api.Content;
using Wmsfo.Api.Data;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.IntegrationTests;

// A116 video media: upload-url takes video/mp4 up to 500 MB; confirm reads an
// MP4 by ranges (moov first and moov last fixtures) and refuses a non-MP4, no
// moov, no video track, and a zero duration with the reason; PATCH
// posterMediaId and the video exclusions for dark, small, and poster; the
// poster in usage, impact, delete, and the orphan collector; the kind=video
// filter; a video passes in a media block and a media item and fails as a
// hero icon, a page icon, and the site logo on write and on publish; the
// snapshot carries the video entry and its poster.
public sealed class A116MediaVideoTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A14Host? _host;

    public A116MediaVideoTests(PostgresFixture fixture)
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

    // ---------- upload-url ----------

    [Fact]
    public async Task Upload_url_takes_video_mp4_up_to_500_MB_and_refuses_over_it()
    {
        var at = await TicketAsync("clip.mp4", "video/mp4", AdminMediaEndpoints.VideoLimit);
        Assert.Equal(HttpStatusCode.Created, at.StatusCode);
        using (var body = await ReadJsonAsync(at))
        {
            var media = body.RootElement.GetProperty("media");
            Assert.Equal("video", media.GetProperty("kind").GetString());
            Assert.Equal("pending", media.GetProperty("state").GetString());
            Assert.Equal("video/mp4", body.RootElement.GetProperty("headers").GetProperty("Content-Type").GetString());
            Assert.Equal(JsonValueKind.Null, media.GetProperty("durationMs").ValueKind);
            Assert.Equal(JsonValueKind.Null, media.GetProperty("posterMediaId").ValueKind);
        }

        var over = await TicketAsync("clip.mp4", "video/mp4", AdminMediaEndpoints.VideoLimit + 1);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);

        var wrongExtension = await TicketAsync("clip.mov", "video/mp4", 1000);
        Assert.Equal(HttpStatusCode.BadRequest, wrongExtension.StatusCode);
    }

    // ---------- confirm ----------

    [Fact]
    public async Task Confirm_reads_a_moov_first_mp4()
    {
        var bytes = Mp4Fixture.MoovFirst(1280, 720, timescale: 600, duration: 25_500);
        var id = await UploadAsync("moov-first.mp4", "video/mp4", bytes);

        var confirm = await SendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        using var body = await ReadJsonAsync(confirm);
        var media = body.RootElement;
        Assert.Equal("ready", media.GetProperty("state").GetString());
        Assert.Equal("video", media.GetProperty("kind").GetString());
        Assert.Equal(1280, media.GetProperty("width").GetInt32());
        Assert.Equal(720, media.GetProperty("height").GetInt32());
        Assert.Equal(42_500, media.GetProperty("durationMs").GetInt32());
        Assert.Equal(bytes.LongLength, media.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), media.GetProperty("sha256").GetString());
        Assert.Empty(media.GetProperty("variants").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, media.GetProperty("dziUrl").ValueKind);

        // The pending tag is gone and nothing but the original was written.
        Assert.Null(await _host!.Store.GetObjectTaggingAsync($"media/{id}/moov-first.mp4"));
        var keys = new List<string>();
        await foreach (var entry in _host.Store.ListPrefixAsync($"media/{id}/")) keys.Add(entry.Key);
        Assert.Equal(new[] { $"media/{id}/moov-first.mp4" }, keys);
    }

    [Fact]
    public async Task Confirm_reads_a_moov_last_mp4()
    {
        var bytes = Mp4Fixture.MoovLast(640, 360, timescale: 1000, duration: 3_250, mdatBytes: 64 * 1024);
        var id = await UploadAsync("moov-last.mp4", "video/mp4", bytes);

        var confirm = await SendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        using var body = await ReadJsonAsync(confirm);
        Assert.Equal(640, body.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(360, body.RootElement.GetProperty("height").GetInt32());
        Assert.Equal(3_250, body.RootElement.GetProperty("durationMs").GetInt32());
    }

    [Fact]
    public async Task Confirm_refuses_a_file_that_is_not_mp4_and_deletes_the_object_and_the_row()
    {
        var id = await UploadAsync("fake.mp4", "video/mp4", BuildPng(32, 32));
        var confirm = await SendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", null);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        using (var body = await ReadJsonAsync(confirm))
        {
            Assert.Equal(ApiErrorCodes.ValidationFailed, body.RootElement.GetProperty("code").GetString());
            Assert.True(body.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty("file", out _));
        }
        await AssertGoneAsync(id, "fake.mp4");
    }

    [Theory]
    [InlineData("moov")]
    [InlineData("track")]
    [InlineData("duration")]
    public async Task Confirm_refuses_with_the_reason(string reason)
    {
        var bytes = reason switch
        {
            "moov" => Mp4Fixture.NoMoov(),
            "track" => Mp4Fixture.MoovFirst(0, 0, timescale: 1000, duration: 5_000),
            _ => Mp4Fixture.MoovFirst(320, 240, timescale: 1000, duration: 0),
        };
        var id = await UploadAsync($"refused-{reason}.mp4", "video/mp4", bytes);
        var confirm = await SendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", null);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        using (var body = await ReadJsonAsync(confirm))
        {
            Assert.Equal(ApiErrorCodes.ValidationFailed, body.RootElement.GetProperty("code").GetString());
            var details = body.RootElement.GetProperty("details");
            Assert.Equal(reason, details.GetProperty("reason").GetString());
            Assert.True(details.GetProperty("fields").TryGetProperty("file", out _));
        }
        await AssertGoneAsync(id, $"refused-{reason}.mp4");
    }

    [Fact]
    public async Task Confirm_refuses_an_object_over_500_MB_with_413()
    {
        var bytes = Mp4Fixture.MoovFirst(320, 240, timescale: 1000, duration: 1_000);
        var id = await UploadAsync("huge.mp4", "video/mp4", bytes);
        // The object grows past the limit after the ticket (a sparse file).
        var path = Path.Combine(_host!.StoreRoot, "media", id, "huge.mp4");
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(AdminMediaEndpoints.VideoLimit + 1);
        }

        var confirm = await SendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", null);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, confirm.StatusCode);
        await AssertGoneAsync(id, "huge.mp4");
    }

    // ---------- PATCH, usage, impact, delete, orphans, filter ----------

    [Fact]
    public async Task Patch_poster_sets_and_clears_and_refuses_what_the_rules_refuse()
    {
        var video = await UploadVideoAsync("clip.mp4");
        var other = await UploadVideoAsync("other.mp4");
        var poster = await UploadPngAsync("poster.png");
        var image = await UploadPngAsync("image.png");
        var svg = await UploadSvgAsync("mark.svg");

        var set = await PatchAsync(video, $"{{\"posterMediaId\":\"{poster}\"}}");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        using (var body = await ReadJsonAsync(set))
            Assert.Equal(poster, body.RootElement.GetProperty("posterMediaId").GetString());

        // A PATCH without the key leaves the poster as it is.
        var alt = await PatchAsync(video, "{\"alt\":\"liftoff\"}");
        using (var body = await ReadJsonAsync(alt))
            Assert.Equal(poster, body.RootElement.GetProperty("posterMediaId").GetString());

        // 404 unknown, 409 pending, 400 the asset itself, a target that is not
        // a video, a poster that is not raster or gif.
        var missing = await PatchAsync(video, $"{{\"posterMediaId\":\"{Guid.NewGuid()}\"}}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var pendingTicket = await ReadJsonAsync(await TicketAsync("pending.png", "image/png", 100)))
        {
            var pending = pendingTicket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
            var notReady = await PatchAsync(video, $"{{\"posterMediaId\":\"{pending}\"}}");
            Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
            Assert.Equal("media_not_ready", await ReadCodeAsync(notReady));
        }
        await AssertFieldRefusedAsync(await PatchAsync(video, $"{{\"posterMediaId\":\"{video}\"}}"), "posterMediaId");
        await AssertFieldRefusedAsync(await PatchAsync(image, $"{{\"posterMediaId\":\"{poster}\"}}"), "posterMediaId");
        await AssertFieldRefusedAsync(await PatchAsync(video, $"{{\"posterMediaId\":\"{other}\"}}"), "posterMediaId");
        await AssertFieldRefusedAsync(await PatchAsync(video, $"{{\"posterMediaId\":\"{svg}\"}}"), "posterMediaId");

        // A video never takes a dark or small version and is never one.
        await AssertFieldRefusedAsync(await PatchAsync(video, $"{{\"darkMediaId\":\"{image}\"}}"), "darkMediaId");
        await AssertFieldRefusedAsync(await PatchAsync(video, $"{{\"smallMediaId\":\"{image}\"}}"), "smallMediaId");
        await AssertFieldRefusedAsync(await PatchAsync(image, $"{{\"darkMediaId\":\"{video}\"}}"), "darkMediaId");
        await AssertFieldRefusedAsync(await PatchAsync(image, $"{{\"smallMediaId\":\"{video}\"}}"), "smallMediaId");

        using (var still = await GetJsonAsync($"/admin/media/{video}"))
        {
            Assert.Equal(poster, still.RootElement.GetProperty("posterMediaId").GetString());
            Assert.Equal(JsonValueKind.Null, still.RootElement.GetProperty("darkMediaId").ValueKind);
        }

        var clear = await PatchAsync(video, "{\"posterMediaId\":null}");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using (var body = await ReadJsonAsync(clear))
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("posterMediaId").ValueKind);
    }

    [Fact]
    public async Task A_poster_shows_in_usage_and_impact_and_its_delete_unlinks_it()
    {
        var video = await UploadVideoAsync("liftoff.mp4");
        var poster = await UploadPngAsync("liftoff-poster.png");
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(video, $"{{\"posterMediaId\":\"{poster}\"}}")).StatusCode);

        using (var usage = await GetJsonAsync($"/admin/media/{poster}/usage"))
        {
            var entry = Assert.Single(usage.RootElement.GetProperty("posterOf").EnumerateArray());
            Assert.Equal(video, entry.GetProperty("id").GetString());
            Assert.Equal("liftoff.mp4", entry.GetProperty("filename").GetString());
        }

        using (var impact = await GetJsonAsync($"/admin/media/{poster}/impact"))
        {
            var group = Assert.Single(impact.RootElement.GetProperty("unlinks").EnumerateArray(),
                g => g.GetProperty("entity").GetString() == "media_asset");
            Assert.Equal(new[] { "poster of liftoff.mp4" },
                group.GetProperty("names").EnumerateArray().Select(n => n.GetString()).ToArray());
        }

        // The orphan collector counts a ready video's poster as referenced.
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var rewind = new NpgsqlCommand(
                "update media_asset set unreferenced_since = now() - interval '31 days' where id = $1;", conn))
            {
                rewind.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(poster) });
                await rewind.ExecuteNonQueryAsync();
            }
            var collector = new MediaOrphanCollector(
                TestConnections.For(_fixture.ConnectionString), _host!.Store,
                NullLogger<MediaOrphanCollector>.Instance);
            await collector.RunOnceAsync(CancellationToken.None);
            await using var read = new NpgsqlCommand(
                "select state, unreferenced_since from media_asset where id = $1;", conn);
            read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(poster) });
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("ready", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
        }

        var delete = await SendAsync(HttpMethod.Delete, $"/admin/media/{poster}", null);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var after = await GetJsonAsync($"/admin/media/{video}");
        Assert.Equal(JsonValueKind.Null, after.RootElement.GetProperty("posterMediaId").ValueKind);
    }

    [Fact]
    public async Task The_kind_filter_lists_videos()
    {
        var video = await UploadVideoAsync("filter.mp4");
        await UploadPngAsync("filter.png");

        using var list = await GetJsonAsync("/admin/media?kind=video");
        var items = list.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var only = Assert.Single(items);
        Assert.Equal(video, only.GetProperty("id").GetString());
        Assert.Equal(42_500, only.GetProperty("durationMs").GetInt32());

        var bad = await SendAsync(HttpMethod.Get, "/admin/media?kind=film", null);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ---------- the reference rule ----------

    [Fact]
    public async Task A_video_passes_in_a_media_block_and_a_media_item_and_reaches_the_snapshot_with_its_poster()
    {
        await BootstrapFirstBootAsync();
        var video = await UploadVideoAsync("block.mp4");
        var poster = await UploadPngAsync("block-poster.png");
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(video, $"{{\"posterMediaId\":\"{poster}\"}}")).StatusCode);
        var pageId = await FindRolePageIdAsync("no_event");

        var richText = await SendAsync(HttpMethod.Post, $"/admin/pages/{pageId}/sections",
            $"{{\"kind\":\"rich_text\",\"data\":{{\"blocks\":[{{\"kind\":\"media\",\"media\":{{\"mediaId\":\"{video}\",\"alt\":null}},\"caption\":null,\"size\":\"full\"}}]}}}}");
        Assert.Equal(HttpStatusCode.Created, richText.StatusCode);

        var mediaSection = await SendAsync(HttpMethod.Post, $"/admin/pages/{pageId}/sections",
            "{\"kind\":\"media\",\"data\":{\"layout\":\"single\",\"columns\":2}}");
        Assert.Equal(HttpStatusCode.Created, mediaSection.StatusCode);
        long mediaSectionId;
        using (var body = await ReadJsonAsync(mediaSection)) mediaSectionId = body.RootElement.GetProperty("id").GetInt64();
        var item = await SendAsync(HttpMethod.Post, $"/admin/sections/{mediaSectionId}/items",
            $"{{\"data\":{{\"media\":{{\"mediaId\":\"{video}\",\"alt\":null}},\"caption\":null,\"link\":null}}}}");
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);

        var publish = await SendAsync(HttpMethod.Post, "/admin/content/publish", "{}");
        Assert.Equal(HttpStatusCode.Created, publish.StatusCode);

        using var snap = JsonDocument.Parse((await GetLatestSnapshotBytesAsync())!);
        var media = snap.RootElement.GetProperty("media");
        var entry = media.GetProperty(video);
        Assert.Equal("video", entry.GetProperty("kind").GetString());
        Assert.Equal(42_500, entry.GetProperty("durationMs").GetInt32());
        Assert.Equal(poster, entry.GetProperty("posterMediaId").GetString());
        Assert.Empty(entry.GetProperty("variants").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("dzi").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("dark").ValueKind);
        Assert.False(entry.GetProperty("invertInDark").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("small").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("smallMediaId").ValueKind);
        // The poster rides in the map as its own entry.
        var posterEntry = media.GetProperty(poster);
        Assert.Equal("raster", posterEntry.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, posterEntry.GetProperty("durationMs").ValueKind);

        // The draft document's media map carries both as well.
        using var draft = await GetJsonAsync("/admin/content/draft");
        Assert.True(draft.RootElement.GetProperty("media").TryGetProperty(video, out _));
        Assert.True(draft.RootElement.GetProperty("media").TryGetProperty(poster, out _));
    }

    [Fact]
    public async Task A_video_as_a_hero_icon_fails_the_section_write_and_the_publish()
    {
        await BootstrapFirstBootAsync();
        var video = await UploadVideoAsync("hero.mp4");
        var pageId = await FindRolePageIdAsync("no_event");
        var heroData = $"{{\"title\":\"Hi\",\"tagline\":null,\"icon\":{{\"source\":\"media\",\"id\":\"{video}\"}},\"links\":[],\"height\":\"short\"}}";

        var write = await SendAsync(HttpMethod.Post, $"/admin/pages/{pageId}/sections",
            $"{{\"kind\":\"hero\",\"data\":{heroData}}}");
        Assert.Equal(HttpStatusCode.BadRequest, write.StatusCode);
        using (var body = await ReadJsonAsync(write))
        {
            Assert.Equal(ApiErrorCodes.ValidationFailed, body.RootElement.GetProperty("code").GetString());
            var fields = body.RootElement.GetProperty("details").GetProperty("fields");
            Assert.Equal($"media asset `{video}` is a video and cannot be used here",
                fields.GetProperty("/data/icon").GetString());
        }

        // A video as a section background is refused the same way.
        var background = await SendAsync(HttpMethod.Post, $"/admin/pages/{pageId}/sections",
            "{\"kind\":\"divider\",\"data\":{\"style\":\"line\"},\"presentation\":{\"width\":\"full\",\"align\":\"center\"," +
            $"\"background\":{{\"kind\":\"media\",\"media\":{{\"mediaId\":\"{video}\",\"alt\":null}},\"overlay\":0}}," +
            "\"spacing\":\"normal\",\"iconBefore\":null,\"iconAfter\":null,\"anchor\":null}}");
        Assert.Equal(HttpStatusCode.BadRequest, background.StatusCode);

        // A hero that reached the working set without the write check fails
        // publish with the same problem.
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "update section set data = $1::jsonb where kind = 'hero' and page_id = $2;", conn);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = heroData });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pageId });
            Assert.True(await cmd.ExecuteNonQueryAsync() > 0);
        }
        var publish = await SendAsync(HttpMethod.Post, "/admin/content/publish", "{}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, publish.StatusCode);
        using var problems = await ReadJsonAsync(publish);
        Assert.Equal(ApiErrorCodes.ContentInvalid, problems.RootElement.GetProperty("code").GetString());
        Assert.Contains(problems.RootElement.GetProperty("details").GetProperty("problems").EnumerateArray(),
            p => p.GetProperty("path").GetString() == "/data/icon"
                && p.GetProperty("message").GetString() == $"media asset `{video}` is a video and cannot be used here");
    }

    [Fact]
    public async Task A_video_is_refused_as_a_page_icon_the_site_logo_and_a_media_icon_in_a_block()
    {
        var video = await UploadVideoAsync("elsewhere.mp4");
        var pageId = await FindRolePageIdAsync("ended");

        await AssertFieldRefusedAsync(
            await SendAsync(HttpMethod.Patch, $"/admin/pages/{pageId}", $"{{\"icon\":{{\"source\":\"media\",\"id\":\"{video}\"}}}}"),
            "icon.id");

        await AssertFieldRefusedAsync(
            await SendAsync(HttpMethod.Put, "/admin/site-settings",
                $"{{\"data\":{{\"logoMedia\":{{\"mediaId\":\"{video}\",\"alt\":null}}}}}}"),
            "/logoMedia/mediaId");

        // An icon block is not a media block.
        await AssertFieldRefusedAsync(
            await SendAsync(HttpMethod.Post, $"/admin/pages/{pageId}/sections",
                $"{{\"kind\":\"rich_text\",\"data\":{{\"blocks\":[{{\"kind\":\"icon\",\"icon\":{{\"source\":\"media\",\"id\":\"{video}\"}},\"size\":\"md\",\"align\":\"center\"}}]}}}}"),
            "/data/blocks/0/icon");
    }

    // ---------- helpers ----------

    private async Task AssertGoneAsync(string id, string filename)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/media/{id}", null)).StatusCode);
        Assert.Null(await _host!.Store.HeadObjectAsync($"media/{id}/{filename}"));
    }

    private static async Task AssertFieldRefusedAsync(HttpResponseMessage response, string field)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400: {(int)response.StatusCode} {text}");
        using var body = JsonDocument.Parse(text);
        Assert.Equal(ApiErrorCodes.ValidationFailed, body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty(field, out _),
            $"expected a problem on `{field}`: {text}");
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

    private Task<string> UploadVideoAsync(string filename) =>
        UploadAndConfirmAsync(filename, "video/mp4", Mp4Fixture.MoovFirst(1280, 720, timescale: 600, duration: 25_500));

    private Task<string> UploadPngAsync(string filename) =>
        UploadAndConfirmAsync(filename, "image/png", BuildPng(64, 64));

    private Task<string> UploadSvgAsync(string filename) =>
        UploadAndConfirmAsync(filename, "image/svg+xml",
            Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"><rect width=\"1\" height=\"1\"/></svg>"));

    private async Task<string> UploadAndConfirmAsync(string filename, string contentType, byte[] bytes)
    {
        var id = await UploadAsync(filename, contentType, bytes);
        var confirm = await SendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        return id;
    }

    // A ticket and the PUT; answers the pending id.
    private async Task<string> UploadAsync(string filename, string contentType, byte[] bytes)
    {
        var ticketResp = await TicketAsync(filename, contentType, bytes.LongLength);
        Assert.Equal(HttpStatusCode.Created, ticketResp.StatusCode);
        using var ticket = await ReadJsonAsync(ticketResp);
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        var uri = new Uri(ticket.RootElement.GetProperty("uploadUrl").GetString()!);
        using var put = new HttpRequestMessage(HttpMethod.Put, uri.PathAndQuery) { Content = new ByteArrayContent(bytes) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        put.Headers.Add("x-amz-tagging", "state=pending");
        Assert.Equal(HttpStatusCode.NoContent, (await _host!.Client.SendAsync(put)).StatusCode);
        return id;
    }

    private Task<HttpResponseMessage> TicketAsync(string filename, string contentType, long sizeBytes) =>
        SendAsync(HttpMethod.Post, "/admin/media/upload-url",
            $"{{\"filename\":\"{filename}\",\"contentType\":\"{contentType}\",\"sizeBytes\":{sizeBytes},\"alt\":\"\",\"title\":\"\"}}");

    private Task<HttpResponseMessage> PatchAsync(string id, string json) =>
        SendAsync(HttpMethod.Patch, $"/admin/media/{id}", json);

    private static byte[] BuildPng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 200, 255));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
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

    private static async Task<string> ReadCodeAsync(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }
}

// Two small hand-built MP4 files (moov first, moov last after an mdat) and
// the refused shapes: a box stream of ftyp, moov (mvhd version 0 and one trak
// whose tkhd carries the frame size), and mdat.
internal static class Mp4Fixture
{
    public static byte[] MoovFirst(int width, int height, uint timescale, uint duration) =>
        Concat(Ftyp(), Moov(width, height, timescale, duration), Box("mdat", new byte[2048]));

    public static byte[] MoovLast(int width, int height, uint timescale, uint duration, int mdatBytes) =>
        Concat(Ftyp(), Box("mdat", new byte[mdatBytes]), Moov(width, height, timescale, duration));

    public static byte[] NoMoov() => Concat(Ftyp(), Box("free", new byte[8]), Box("mdat", new byte[2048]));

    private static byte[] Moov(int width, int height, uint timescale, uint duration) =>
        Box("moov",
            Box("mvhd", new byte[4], new byte[8], U32(timescale), U32(duration), new byte[80]),
            Box("trak", Box("tkhd", new byte[] { 0, 0, 0, 7 }, new byte[20], new byte[52],
                U32((uint)width << 16), U32((uint)height << 16))));

    private static byte[] Ftyp() =>
        Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isomiso2avc1mp41"));

    private static byte[] Box(string type, params byte[][] body)
    {
        var payload = Concat(body);
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] U32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }
}
