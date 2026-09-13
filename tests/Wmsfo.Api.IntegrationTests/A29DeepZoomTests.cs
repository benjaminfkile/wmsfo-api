using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// A29 acceptance (task 333, api.md 11.3 step 5, contracts 1.3b): the Deep Zoom
// tile pyramid at confirm, `dzi` in the media map.
public sealed class A29DeepZoomTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A29Host? _host;

    public A29DeepZoomTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAsync();
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
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from location;",
            "delete from event;",
            "delete from route;",
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
        await SnapshotSeed.EnsureAsync(conn);
    }

    // ---------- 3000 px raster: pyramid + dziUrl ----------

    [Fact]
    public async Task Confirm_3000_wide_raster_writes_pyramid_and_exposes_dziUrl()
    {
        _host = await A29Host.StartAsync(_fixture.ConnectionString);

        var pngBytes = BuildPng(3000, 2000);
        var id = await UploadAndConfirmAsync("poster.png", "image/png", pngBytes);

        var descriptorPath = Path.Combine(_host!.StoreRoot, "media", id, "dzi", "poster.dzi");
        Assert.True(File.Exists(descriptorPath), "descriptor missing");
        var xml = await File.ReadAllTextAsync(descriptorPath);
        Assert.Contains("TileSize=\"254\"", xml);
        Assert.Contains("Overlap=\"1\"", xml);
        Assert.Contains("Width=\"3000\"", xml);
        Assert.Contains("Height=\"2000\"", xml);

        // A sample of tile levels lands under poster_files/{level}/.
        Assert.True(Directory.Exists(Path.Combine(_host.StoreRoot, "media", id, "dzi", "poster_files", "12")));
        Assert.True(Directory.Exists(Path.Combine(_host.StoreRoot, "media", id, "dzi", "poster_files", "0")));
        // Level 0 is always a single 1_0.jpg tile whose bytes are a JPEG.
        var level0Tile = Path.Combine(_host.StoreRoot, "media", id, "dzi", "poster_files", "0", "0_0.jpg");
        Assert.True(File.Exists(level0Tile));
        var head = await File.ReadAllBytesAsync(level0Tile);
        Assert.True(head.Length > 3);
        Assert.Equal(0xFF, head[0]);
        Assert.Equal(0xD8, head[1]);
        Assert.Equal(0xFF, head[2]);

        // Total tile count matches api.md 11.3 (levels 0..12 for 3000x2000).
        var totalTiles = Directory.EnumerateFiles(
                Path.Combine(_host.StoreRoot, "media", id, "dzi", "poster_files"),
                "*.jpg", SearchOption.AllDirectories)
            .Count();
        Assert.Equal(137, totalTiles);

        // GET /admin/media/{id} returns dziUrl.
        var get = await EditorSendAsync(HttpMethod.Get, $"/admin/media/{id}", content: null);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await ReadJsonAsync(get);
        var dziUrl = body.RootElement.GetProperty("dziUrl").GetString();
        Assert.Equal($"{_host.Options.CdnBaseUrl}/media/{id}/dzi/poster.dzi", dziUrl);
    }

    // ---------- Below threshold: no pyramid, dziUrl null ----------

    [Fact]
    public async Task Confirm_1024_wide_raster_has_no_pyramid_and_null_dziUrl()
    {
        _host = await A29Host.StartAsync(_fixture.ConnectionString);

        var pngBytes = BuildPng(1024, 500);
        var id = await UploadAndConfirmAsync("small.png", "image/png", pngBytes);

        Assert.False(Directory.Exists(Path.Combine(_host!.StoreRoot, "media", id, "dzi")));

        var get = await EditorSendAsync(HttpMethod.Get, $"/admin/media/{id}", content: null);
        var body = await ReadJsonAsync(get);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("dziUrl").ValueKind);

        // dzi_key column stays null in the row.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select dzi_key from media_asset where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(id) });
        var scalar = await cmd.ExecuteScalarAsync();
        Assert.True(scalar is null || scalar is DBNull);
    }

    // ---------- Failed tile PUT: 502, row pending, retry succeeds ----------

    [Fact]
    public async Task Failed_tile_put_leaves_row_pending_and_confirm_retries_cleanly()
    {
        var faulty = new FaultInjectingStore { FailTileKeyContains = "poster_files/10/" };
        _host = await A29Host.StartAsync(_fixture.ConnectionString, local => faulty.Wrap(local));

        var pngBytes = BuildPng(3000, 2000);
        var ticket = await GetTicketAsync("retry.png", "image/png", pngBytes.LongLength);
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        await UploadAsync(ticket.RootElement.GetProperty("uploadUrl").GetString()!, pngBytes, "image/png");

        var confirm = await EditorSendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", content: null);
        Assert.Equal(HttpStatusCode.BadGateway, confirm.StatusCode);
        Assert.Equal("media_write_failed", await ReadCodeAsync(confirm));

        // Row is still pending; the original object is still on disk (the
        // browser PUT survived because it is the local-upload endpoint's write).
        Assert.Equal("pending", await ReadRowStateAsync(id));

        // Fix the store and retry. Confirm succeeds and dzi_key lands on the row.
        faulty.FailTileKeyContains = null;
        var confirm2 = await EditorSendAsync(HttpMethod.Post, $"/admin/media/{id}/confirm", content: null);
        Assert.Equal(HttpStatusCode.OK, confirm2.StatusCode);
        var confirmed = await ReadJsonAsync(confirm2);
        Assert.Equal("ready", confirmed.RootElement.GetProperty("state").GetString());
        Assert.False(string.IsNullOrEmpty(confirmed.RootElement.GetProperty("dziUrl").GetString()));
    }

    // ---------- Delete removes the tile pyramid too ----------

    [Fact]
    public async Task Delete_removes_the_dzi_pyramid()
    {
        _host = await A29Host.StartAsync(_fixture.ConnectionString);

        var pngBytes = BuildPng(3000, 2000);
        var id = await UploadAndConfirmAsync("gone.png", "image/png", pngBytes);

        var dziDir = Path.Combine(_host!.StoreRoot, "media", id, "dzi");
        Assert.True(Directory.Exists(dziDir));

        var response = await EditorSendAsync(HttpMethod.Delete, $"/admin/media/{id}", content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(dziDir, "poster.dzi")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_host.StoreRoot, "media", id), "*", SearchOption.AllDirectories));
    }

    // ---------- Snapshot media map carries `dzi` for the tiled asset ----------

    [Fact]
    public async Task Snapshot_media_map_carries_dzi_for_tiled_asset()
    {
        _host = await A29Host.StartAsync(_fixture.ConnectionString);

        var pngBytes = BuildPng(3000, 2000);
        var id = await UploadAndConfirmAsync("poster.png", "image/png", pngBytes);

        // Reference the asset from the current event's route poster so the
        // snapshot builder picks it up (the media map includes event.routeImage).
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var ins = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, route_image_media_id, created_by)
values (2027, 'Flyover 2027', 1, true, $1, 'test');", conn);
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(id) });
            await ins.ExecuteNonQueryAsync();
        }

        var builder = _host.App.Services.GetRequiredService<SnapshotBuilder>();
        var info = await builder.RebuildAsync(CancellationToken.None);

        var bytes = await _host.Store.GetObjectAsync(info.Key, CancellationToken.None);
        Assert.NotNull(bytes);
        using var doc = JsonDocument.Parse(bytes!.Bytes);
        var mediaEntry = doc.RootElement.GetProperty("media").GetProperty(id);
        var dzi = mediaEntry.GetProperty("dzi").GetString();
        Assert.Equal($"{_host.Options.CdnBaseUrl}/media/{id}/dzi/poster.dzi", dzi);
    }

    // ---------- helpers ----------

    private static byte[] BuildPng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(255, 0, 0, 255));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
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

    private async Task<string?> ReadRowStateAsync(string id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select state from media_asset where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(id) });
        var r = await cmd.ExecuteScalarAsync();
        return r is string s ? s : null;
    }

    // Wraps a LocalObjectStore and rejects tile PUTs whose key contains a
    // configured substring. The retry test sets FailTileKeyContains to a level
    // path, verifies the confirm returns 502, then clears the fault and
    // confirms again.
    private sealed class FaultInjectingStore
    {
        public string? FailTileKeyContains { get; set; }

        public IObjectStore Wrap(LocalObjectStore inner) => new Wrapper(inner, this);

        private sealed class Wrapper : IObjectStore
        {
            private readonly LocalObjectStore _inner;
            private readonly FaultInjectingStore _cfg;
            public Wrapper(LocalObjectStore inner, FaultInjectingStore cfg) { _inner = inner; _cfg = cfg; }
            public Task PutObjectAsync(string key, ReadOnlyMemory<byte> bytes, string contentType, string cacheControl, string? tag = null, CancellationToken cancellationToken = default)
            {
                var fault = _cfg.FailTileKeyContains;
                if (fault is not null && key.Contains(fault, StringComparison.Ordinal))
                    throw new IOException("simulated PUT failure for " + key);
                return _inner.PutObjectAsync(key, bytes, contentType, cacheControl, tag, cancellationToken);
            }
            public Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteObjectAsync(key, cancellationToken);
            public IAsyncEnumerable<ObjectListEntry> ListPrefixAsync(string prefix, CancellationToken cancellationToken = default) => _inner.ListPrefixAsync(prefix, cancellationToken);
            public Task PutObjectTaggingAsync(string key, string tag, CancellationToken cancellationToken = default) => _inner.PutObjectTaggingAsync(key, tag, cancellationToken);
            public Task DeleteObjectTaggingAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteObjectTaggingAsync(key, cancellationToken);
            public Task<string?> GetObjectTaggingAsync(string key, CancellationToken cancellationToken = default) => _inner.GetObjectTaggingAsync(key, cancellationToken);
            public Task<ObjectHead?> HeadObjectAsync(string key, CancellationToken cancellationToken = default) => _inner.HeadObjectAsync(key, cancellationToken);
            public Task<ObjectContent?> GetObjectAsync(string key, CancellationToken cancellationToken = default) => _inner.GetObjectAsync(key, cancellationToken);
            public string PresignPut(string key, string contentType, string tag) => _inner.PresignPut(key, contentType, tag);
        }
    }
}
