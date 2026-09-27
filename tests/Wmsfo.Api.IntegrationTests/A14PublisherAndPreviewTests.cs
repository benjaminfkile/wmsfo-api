using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.IntegrationTests;

// A14 acceptance criteria:
//   - publish with publish-level problems -> 422 content_invalid + ProblemRef[]
//   - publish with no changes since the last version -> 409 content_unchanged
//   - publish success: newest version is pruned at 51, snapshot embeds the
//     document, media map exactly the referenced assets
//   - restore recreates the six role pages
//   - preview token expires after 15 minutes
//   - first boot on an empty database ends with version 1, snapshot version 1,
//     and a live object
public sealed class A14PublisherAndPreviewTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A14Host? _host;

    public A14PublisherAndPreviewTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAsync();
        // Reset the working set + content_version + snapshot between tests so state
        // does not leak across the class-scoped fixture.
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

        // Seed the working set via the same first-boot path the fleet uses.
        var starter = _host.GetService<StarterContent>();
        await using var conn2 = new NpgsqlConnection(_fixture.ConnectionString);
        await conn2.OpenAsync();
        await starter.EnsureSeededAsync(conn2, default);
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
    }

    // -------------------- publish --------------------

    [Fact]
    public async Task Publish_success_inserts_version_and_updates_snapshot()
    {
        // First, seed a snapshot row so the publish transaction has something
        // to lock. First-boot inserts snapshot v1 with the same starter content
        // that's already in the working set, so publish immediately after would
        // return content_unchanged. Instead we mutate the working set slightly.
        await BootstrapFirstBootAsync();

        // Change something so the second publish is not content_unchanged.
        await BumpSiteSettingsSiteNameAsync("Second Publish");

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("id").GetInt64() > 0);
        Assert.Equal(DevStaticTokens.EditorEmail, doc.RootElement.GetProperty("publishedBy").GetString());
        // sha256 is a canonical 64-hex string.
        Assert.Equal(64, doc.RootElement.GetProperty("sha256").GetString()!.Length);
        // Both pages and sections are counted from the built document.
        Assert.True(doc.RootElement.GetProperty("pageCount").GetInt32() >= 12);
        Assert.True(doc.RootElement.GetProperty("sectionCount").GetInt32() >= 12);

        // Snapshot embeds the newest content_version document. Compare canonical
        // bytes: `content_version.document` is jsonb (server-normalized), so the
        // embedded object is re-serialized from the DTO with the API's canonical
        // options. Both paths yield the same sha256.
        var (newestSha, _) = await ReadNewestVersionAsync();
        var snapshotBytes = await GetLatestSnapshotBytesAsync();
        Assert.NotNull(snapshotBytes);
        using var snapDoc = JsonDocument.Parse(snapshotBytes!);
        var embedded = snapDoc.RootElement.GetProperty("content");
        // Serialize the embedded content back through canonical options so the
        // comparison ignores whitespace differences.
        var reserialized = CanonicalJson.SerializeToUtf8Bytes(
            JsonSerializer.Deserialize<ContentDocument>(embedded.GetRawText(),
                new JsonSerializerOptions(CanonicalJson.Options)
                {
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
                })!);
        var embeddedSha = CanonicalJson.Sha256Hex(reserialized);
        Assert.Equal(newestSha, embeddedSha);
    }

    [Fact]
    public async Task Publish_unchanged_returns_409_content_unchanged()
    {
        // First-boot publishes the starter content. Publishing again with no
        // working-set change returns 409 content_unchanged.
        await BootstrapFirstBootAsync();

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, "expected 409 Conflict but got " + response.StatusCode + " body=" + body);
        Assert.Equal(ApiErrorCodes.ContentUnchanged, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Publish_with_problems_returns_422_content_invalid()
    {
        // Introduce a publish-level problem: replace one section's data with
        // an object that has no `title` (hero requires title at publish). The
        // draft-level accepted it because required is stripped.
        await BootstrapFirstBootAsync();

        // Locate any hero section and null its title so the publish schema fails.
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
update section set data = jsonb_build_object(
  'height', 'short', 'links', '[]'::jsonb, 'icon', null, 'tagline', null
) where kind = 'hero';", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(ApiErrorCodes.ContentInvalid, doc.RootElement.GetProperty("code").GetString());
        var problems = doc.RootElement.GetProperty("details").GetProperty("problems").EnumerateArray().ToArray();
        Assert.NotEmpty(problems);
        // At least one problem carries a pageId + sectionId.
        Assert.Contains(problems, p =>
            p.GetProperty("pageId").ValueKind != JsonValueKind.Null &&
            p.GetProperty("sectionId").ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Publish_prunes_to_newest_fifty()
    {
        // Seed 51 content_version rows directly. Publishing once more prunes
        // to 50 by deleting the oldest.
        await BootstrapFirstBootAsync();

        // Insert 50 additional rows with distinct hashes.
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            for (var i = 2; i <= 50; i++)
            {
                var payload = "{\"n\":" + i + "}";
                var sha = HexFor(i);
                await using var ins = new NpgsqlCommand(@"
insert into content_version (document, sha256, media_ids, label, published_by)
values ($1::jsonb, $2, '{}'::uuid[], null, 'test');", conn);
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = payload });
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = sha });
                await ins.ExecuteNonQueryAsync();
            }
        }

        var (initialMinId, initialCount) = await ReadVersionCountAsync();
        Assert.Equal(50, initialCount);

        // Bump the working set so publish is not unchanged, then publish.
        await BumpSiteSettingsSiteNameAsync("Prune Test");
        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var (afterMinId, afterCount) = await ReadVersionCountAsync();
        Assert.Equal(50, afterCount);
        // The oldest row is gone (min id moved up).
        Assert.True(afterMinId > initialMinId,
            $"expected min id to advance from {initialMinId}; got {afterMinId}");
    }

    // A14f (task 284): the snapshot's media map holds exactly the referenced
    // media assets - one from content, one sponsor logo, one cookie-type media
    // icon - and the fourth ready asset is absent because nothing references it.
    // content_version.media_ids for the newest version equals the
    // content-referenced ids only; sponsor logos and cookie icons feed the
    // snapshot map but not the version's media_ids column.
    [Fact]
    public async Task Publish_media_map_keys_are_exactly_the_referenced_ids_and_unreferenced_asset_is_absent()
    {
        await BootstrapFirstBootAsync();

        // Push four ready assets through the real pipeline. raster1 will be
        // referenced from a content section, raster2 from a sponsor, svg from
        // a cookie type; the fourth stays unreferenced.
        var raster1 = await UploadAndConfirmRasterAsync("hero.png", 1024, 512);
        var raster2 = await UploadAndConfirmRasterAsync("logo.png", 1024, 512);
        var svg = await UploadAndConfirmSvgAsync("cookie.svg");
        var unreferenced = await UploadAndConfirmRasterAsync("orphan.png", 700, 400);

        // Reference raster1 from the about page's rich_text section (MediaRef
        // inside a media block).
        await AddMediaBlockToAboutPageAsync(raster1);

        // Wire raster2 as a sponsor's logo. The snapshot only pulls sponsors
        // joined to the current event's year, so also create an event and a
        // sponsor_year row for that sponsor.
        await SeedCurrentEventAsync(2027);
        await SeedSponsorWithLogoAsync("Fuel Co", raster2, 2027);

        // Wire the svg as a cookie type's media icon.
        await SeedCookieTypeWithMediaIconAsync("Snickerdoodle", svg);

        // Bump site settings so the publish is not content_unchanged.
        await BumpSiteSettingsSiteNameAsync("Media map test");

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Read the newest snapshot's bytes off the local store.
        var snapshotBytes = await GetLatestSnapshotBytesAsync();
        Assert.NotNull(snapshotBytes);
        using var snap = JsonDocument.Parse(snapshotBytes!);
        var media = snap.RootElement.GetProperty("media");

        // The media map's keys are exactly the three referenced ids, sorted
        // ordinal; the unreferenced asset is absent.
        var mediaKeys = media.EnumerateObject().Select(p => p.Name).ToArray();
        var expectedKeys = new[] { raster1, raster2, svg }
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedKeys, mediaKeys);
        Assert.DoesNotContain(unreferenced, mediaKeys);

        // Each entry carries the variant urls and dimensions the builder writes.
        var cdn = _host.Options.CdnBaseUrl.TrimEnd('/');
        var raster1Entry = media.GetProperty(raster1);
        Assert.Equal("raster", raster1Entry.GetProperty("kind").GetString());
        Assert.Equal(1024, raster1Entry.GetProperty("width").GetInt32());
        Assert.Equal(512, raster1Entry.GetProperty("height").GetInt32());
        Assert.Equal(cdn + "/media/" + raster1 + "/hero.png",
            raster1Entry.GetProperty("url").GetString());
        var raster1Variants = raster1Entry.GetProperty("variants");
        Assert.Equal(cdn + "/media/" + raster1 + "/w480.webp",
            raster1Variants.GetProperty("480").GetString());
        Assert.Equal(cdn + "/media/" + raster1 + "/w960.webp",
            raster1Variants.GetProperty("960").GetString());

        var raster2Entry = media.GetProperty(raster2);
        Assert.Equal("raster", raster2Entry.GetProperty("kind").GetString());
        Assert.Equal(1024, raster2Entry.GetProperty("width").GetInt32());
        Assert.Equal(512, raster2Entry.GetProperty("height").GetInt32());
        var raster2Variants = raster2Entry.GetProperty("variants");
        Assert.Equal(cdn + "/media/" + raster2 + "/w480.webp",
            raster2Variants.GetProperty("480").GetString());
        Assert.Equal(cdn + "/media/" + raster2 + "/w960.webp",
            raster2Variants.GetProperty("960").GetString());

        var svgEntry = media.GetProperty(svg);
        Assert.Equal("svg", svgEntry.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, svgEntry.GetProperty("width").ValueKind);
        Assert.Equal(JsonValueKind.Null, svgEntry.GetProperty("height").ValueKind);
        Assert.Empty(svgEntry.GetProperty("variants").EnumerateObject());

        // content_version.media_ids for the newest version equals only the
        // content-referenced ids (raster1). Sponsor logos and cookie icons feed
        // the snapshot map but not the version's media_ids column.
        var contentMediaIds = await ReadNewestVersionMediaIdsAsync();
        Assert.Equal(new[] { raster1 }, contentMediaIds);
    }

    // A47: a hero whose `icon` is a ready raster media asset publishes without
    // problems and the snapshot's `media` map carries that asset so the site
    // can resolve it. The media icon may be any ready image, not only an SVG.
    [Fact]
    public async Task Publish_hero_icon_ready_raster_media_publishes_and_snapshot_media_carries_it()
    {
        await BootstrapFirstBootAsync();

        var raster = await UploadAndConfirmRasterAsync("hero-icon.png", 512, 512);
        await SetHeroIconMediaAsync(raster);
        await BumpSiteSettingsSiteNameAsync("Hero raster icon");

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var snapshotBytes = await GetLatestSnapshotBytesAsync();
        Assert.NotNull(snapshotBytes);
        using var snap = JsonDocument.Parse(snapshotBytes!);
        var media = snap.RootElement.GetProperty("media");
        var entry = media.GetProperty(raster);
        Assert.Equal("raster", entry.GetProperty("kind").GetString());
        Assert.Equal(512, entry.GetProperty("width").GetInt32());
        Assert.Equal(512, entry.GetProperty("height").GetInt32());
    }

    // A47: a hero whose `icon` is a media source pointing at a non-existent
    // media asset is a publish-level problem.
    [Fact]
    public async Task Publish_hero_icon_missing_media_is_a_problem()
    {
        await BootstrapFirstBootAsync();
        var missing = Guid.NewGuid().ToString();
        await SetHeroIconMediaAsync(missing);

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(ApiErrorCodes.ContentInvalid, doc.RootElement.GetProperty("code").GetString());
        var problems = doc.RootElement.GetProperty("details").GetProperty("problems").EnumerateArray().ToArray();
        Assert.Contains(problems, p =>
            (p.GetProperty("message").GetString() ?? "").Contains(missing, StringComparison.Ordinal));
    }

    // -------------------- presentation card and iconSize --------------------

    // A section PATCH with `card` and `iconSize` stores both keys, the admin page
    // read returns them, and the published document and the snapshot carry them.
    [Fact]
    public async Task Presentation_card_and_icon_size_round_trip_to_admin_read_and_published_document()
    {
        await BootstrapFirstBootAsync();
        var (pageId, sectionId) = await FindSectionAsync("about", "rich_text");

        var patch = await PatchSectionAsync(sectionId,
            "{\"presentation\":{\"width\":\"narrow\",\"align\":\"start\",\"background\":{\"kind\":\"none\"},"
            + "\"spacing\":\"normal\",\"iconBefore\":null,\"iconAfter\":null,\"anchor\":null,"
            + "\"card\":false,\"iconSize\":\"lg\"}}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using (var patched = JsonDocument.Parse(await patch.Content.ReadAsStringAsync()))
        {
            var pres = patched.RootElement.GetProperty("presentation");
            Assert.False(pres.GetProperty("card").GetBoolean());
            Assert.Equal("lg", pres.GetProperty("iconSize").GetString());
        }

        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "select presentation->>'card', presentation->>'iconSize' from section where id = $1;", conn);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = sectionId });
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("false", reader.GetString(0));
            Assert.Equal("lg", reader.GetString(1));
        }

        using (var getReq = _host!.EditorRequest(HttpMethod.Get, $"/admin/pages/{pageId}"))
        {
            var get = await _host.Client.SendAsync(getReq);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            using var page = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            var section = page.RootElement.GetProperty("sections").EnumerateArray()
                .Single(s => s.GetProperty("id").GetInt64() == sectionId);
            var pres = section.GetProperty("presentation");
            Assert.False(pres.GetProperty("card").GetBoolean());
            Assert.Equal("lg", pres.GetProperty("iconSize").GetString());
        }

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var (_, versionJson) = await ReadNewestVersionAsync();
        using (var version = JsonDocument.Parse(versionJson))
        {
            var pres = FindPublishedPresentation(version.RootElement, sectionId);
            Assert.False(pres.GetProperty("card").GetBoolean());
            Assert.Equal("lg", pres.GetProperty("iconSize").GetString());
        }

        var snapshotBytes = await GetLatestSnapshotBytesAsync();
        Assert.NotNull(snapshotBytes);
        using var snap = JsonDocument.Parse(snapshotBytes!);
        var content = snap.RootElement.GetProperty("content");
        var snapPres = FindPublishedPresentation(content, sectionId);
        Assert.False(snapPres.GetProperty("card").GetBoolean());
        Assert.Equal("lg", snapPres.GetProperty("iconSize").GetString());
        var keys = snapPres.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "width", "align", "background", "spacing", "iconBefore", "iconAfter", "anchor", "card", "iconSize" }, keys);
    }

    // A presentation without `card` and `iconSize` validates and publishes, and
    // the published presentation leaves both keys out.
    [Fact]
    public async Task Presentation_without_card_and_icon_size_validates_and_publishes()
    {
        await BootstrapFirstBootAsync();
        var (_, sectionId) = await FindSectionAsync("about", "rich_text");

        var patch = await PatchSectionAsync(sectionId,
            "{\"presentation\":{\"width\":\"wide\",\"align\":\"start\",\"background\":{\"kind\":\"none\"},"
            + "\"spacing\":\"loose\",\"iconBefore\":null,\"iconAfter\":null,\"anchor\":null}}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var (_, versionJson) = await ReadNewestVersionAsync();
        using var version = JsonDocument.Parse(versionJson);
        var pres = FindPublishedPresentation(version.RootElement, sectionId);
        Assert.Equal("loose", pres.GetProperty("spacing").GetString());
        Assert.False(pres.TryGetProperty("card", out _));
        Assert.False(pres.TryGetProperty("iconSize", out _));
    }

    [Fact]
    public async Task Presentation_unknown_icon_size_is_400_validation_failed()
    {
        var (_, sectionId) = await FindSectionAsync("about", "rich_text");

        var patch = await PatchSectionAsync(sectionId,
            "{\"presentation\":{\"width\":\"wide\",\"align\":\"start\",\"background\":{\"kind\":\"none\"},"
            + "\"spacing\":\"normal\",\"iconBefore\":null,\"iconAfter\":null,\"anchor\":null,"
            + "\"iconSize\":\"huge\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(patch));
    }

    [Fact]
    public async Task Hero_with_icon_size_xl_publishes()
    {
        await BootstrapFirstBootAsync();
        var (_, sectionId) = await FindSectionAsync("no-event", "hero");

        var patch = await PatchSectionAsync(sectionId,
            "{\"data\":{\"title\":\"Hero\",\"tagline\":null,\"icon\":{\"source\":\"library\",\"id\":\"helicopter\"},"
            + "\"links\":[],\"height\":\"tall\",\"iconSize\":\"xl\"}}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var (_, versionJson) = await ReadNewestVersionAsync();
        using var version = JsonDocument.Parse(versionJson);
        var data = version.RootElement.GetProperty("pages").EnumerateArray()
            .SelectMany(p => p.GetProperty("sections").EnumerateArray())
            .Single(s => s.GetProperty("id").GetInt64() == sectionId)
            .GetProperty("data");
        Assert.Equal("xl", data.GetProperty("iconSize").GetString());
    }

    // -------------------- site logo --------------------

    // Site settings with `logoMedia` naming a ready asset and `headerShowsSiteName`
    // false save and publish; the published settings carry both keys and the
    // snapshot's media map carries the logo asset.
    [Fact]
    public async Task Site_settings_logo_media_and_header_flag_publish_and_snapshot_media_carries_logo()
    {
        await BootstrapFirstBootAsync();
        var logo = await UploadAndConfirmRasterAsync("site-logo.png", 600, 300);

        var put = await PutSiteSettingsAsync(data =>
        {
            data["logoMedia"] = new JsonObject { ["mediaId"] = logo, ["alt"] = "Site logo" };
            data["headerShowsSiteName"] = false;
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var saved = JsonDocument.Parse(await put.Content.ReadAsStringAsync()))
        {
            var data = saved.RootElement.GetProperty("data");
            Assert.Equal(logo, data.GetProperty("logoMedia").GetProperty("mediaId").GetString());
            Assert.False(data.GetProperty("headerShowsSiteName").GetBoolean());
            Assert.Empty(saved.RootElement.GetProperty("problems").EnumerateArray());
        }

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var (_, versionJson) = await ReadNewestVersionAsync();
        using (var version = JsonDocument.Parse(versionJson))
        {
            var settings = version.RootElement.GetProperty("settings");
            var logoMedia = settings.GetProperty("logoMedia");
            Assert.Equal(logo, logoMedia.GetProperty("mediaId").GetString());
            Assert.Equal("Site logo", logoMedia.GetProperty("alt").GetString());
            Assert.False(settings.GetProperty("headerShowsSiteName").GetBoolean());
        }
        Assert.Contains(logo, await ReadNewestVersionMediaIdsAsync());

        var snapshotBytes = await GetLatestSnapshotBytesAsync();
        Assert.NotNull(snapshotBytes);
        using var snap = JsonDocument.Parse(snapshotBytes!);
        var snapSettings = snap.RootElement.GetProperty("content").GetProperty("settings");
        Assert.Equal(logo, snapSettings.GetProperty("logoMedia").GetProperty("mediaId").GetString());
        Assert.False(snapSettings.GetProperty("headerShowsSiteName").GetBoolean());
        var entry = snap.RootElement.GetProperty("media").GetProperty(logo);
        Assert.Equal("raster", entry.GetProperty("kind").GetString());
        Assert.Equal(600, entry.GetProperty("width").GetInt32());
        Assert.Equal(300, entry.GetProperty("height").GetInt32());
    }

    // A51: the published snapshot's entry for an asset with a dark version
    // carries `dark` ({ url, variants } of the dark asset) and `invertInDark`;
    // the dark asset has no entry of its own.
    [Fact]
    public async Task Publish_media_entry_carries_dark_version_and_invert_in_dark()
    {
        await BootstrapFirstBootAsync();
        var logo = await UploadAndConfirmRasterAsync("site-logo.png", 1024, 512);
        var darkLogo = await UploadAndConfirmRasterAsync("site-logo-dark.png", 700, 400);

        using (var patch = _host!.EditorRequest(HttpMethod.Patch, $"/admin/media/{logo}"))
        {
            patch.Content = new StringContent(
                $"{{\"darkMediaId\":\"{darkLogo}\",\"invertInDark\":true}}", Encoding.UTF8, "application/json");
            var patched = await _host.Client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        }

        var put = await PutSiteSettingsAsync(data =>
            data["logoMedia"] = new JsonObject { ["mediaId"] = logo, ["alt"] = "Site logo" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var req = _host.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var snapshotBytes = await GetLatestSnapshotBytesAsync();
        Assert.NotNull(snapshotBytes);
        using var snap = JsonDocument.Parse(snapshotBytes!);
        var media = snap.RootElement.GetProperty("media");
        Assert.False(media.TryGetProperty(darkLogo, out _));

        var cdn = _host.Options.CdnBaseUrl.TrimEnd('/');
        var entry = media.GetProperty(logo);
        Assert.True(entry.GetProperty("invertInDark").GetBoolean());
        var dark = entry.GetProperty("dark");
        Assert.Equal(cdn + "/media/" + darkLogo + "/site-logo-dark.png", dark.GetProperty("url").GetString());
        var darkVariants = dark.GetProperty("variants").EnumerateObject().ToArray();
        var only = Assert.Single(darkVariants);
        Assert.Equal("480", only.Name);
        Assert.Equal(cdn + "/media/" + darkLogo + "/w480.webp", only.Value.GetString());

        // Keys follow the documented order: dark and invertInDark after dzi.
        var keys = entry.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "url", "kind", "width", "height", "alt", "variants", "dzi", "dark", "invertInDark" }, keys);
    }

    // A pending asset as `logoMedia` saves as a draft but is a publish problem
    // at /settings/logoMedia/mediaId.
    [Fact]
    public async Task Site_settings_pending_logo_media_is_a_publish_problem()
    {
        await BootstrapFirstBootAsync();
        var pending = await CreatePendingMediaAsync("pending-logo.png");

        var put = await PutSiteSettingsAsync(data =>
            data["logoMedia"] = new JsonObject { ["mediaId"] = pending, ["alt"] = null });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.ContentInvalid, doc.RootElement.GetProperty("code").GetString());
        var problems = doc.RootElement.GetProperty("details").GetProperty("problems").EnumerateArray().ToArray();
        Assert.Contains(problems, p =>
            p.GetProperty("path").GetString() == "/settings/logoMedia/mediaId"
            && (p.GetProperty("message").GetString() ?? "").Contains("not ready", StringComparison.Ordinal));
    }

    // Site settings without `logoMedia` and `headerShowsSiteName` publish, and
    // the published settings leave both keys out.
    [Fact]
    public async Task Site_settings_without_logo_media_and_header_flag_publish()
    {
        await BootstrapFirstBootAsync();

        var put = await PutSiteSettingsAsync(data =>
        {
            data.Remove("logoMedia");
            data.Remove("headerShowsSiteName");
            data["siteName"] = "No logo media";
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var (_, versionJson) = await ReadNewestVersionAsync();
        using var version = JsonDocument.Parse(versionJson);
        var settings = version.RootElement.GetProperty("settings");
        Assert.Equal("No logo media", settings.GetProperty("siteName").GetString());
        Assert.False(settings.TryGetProperty("logoMedia", out _));
        Assert.False(settings.TryGetProperty("headerShowsSiteName", out _));
    }

    [Fact]
    public async Task Hero_with_show_logo_publishes()
    {
        await BootstrapFirstBootAsync();
        var (_, sectionId) = await FindSectionAsync("no-event", "hero");

        var patch = await PatchSectionAsync(sectionId,
            "{\"data\":{\"title\":\"Hero\",\"tagline\":null,\"icon\":null,"
            + "\"links\":[],\"height\":\"tall\",\"iconSize\":\"lg\",\"showLogo\":true}}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var req = _host!.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var (_, versionJson) = await ReadNewestVersionAsync();
        using var version = JsonDocument.Parse(versionJson);
        var data = version.RootElement.GetProperty("pages").EnumerateArray()
            .SelectMany(p => p.GetProperty("sections").EnumerateArray())
            .Single(s => s.GetProperty("id").GetInt64() == sectionId)
            .GetProperty("data");
        Assert.True(data.GetProperty("showLogo").GetBoolean());
        Assert.Equal("lg", data.GetProperty("iconSize").GetString());
    }

    // -------------------- restore --------------------

    [Fact]
    public async Task Restore_recreates_six_role_pages()
    {
        // Publish once so a content_version row exists.
        await BootstrapFirstBootAsync();
        var versionId = await ReadNewestVersionIdAsync();

        // Wipe the working set (leaving only content_version untouched).
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var del = new NpgsqlCommand("delete from page;", conn);
            await del.ExecuteNonQueryAsync();
        }
        Assert.Equal(0, await ReadPageCountAsync());

        // Restore the version.
        using var req = _host!.EditorRequest(HttpMethod.Post, $"/admin/content/versions/{versionId}/restore");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The six role pages are back with the correct roles.
        var roles = await ReadRolesAsync();
        Assert.Contains("no_event", roles);
        Assert.Contains("planned", roles);
        Assert.Contains("scheduled", roles);
        Assert.Contains("live", roles);
        Assert.Contains("ended", roles);
        Assert.Contains("cancelled", roles);
        // And the ordinary pages come back too (about, sponsors, route, donate, contact, alerts).
        var slugs = await ReadSlugsAsync();
        Assert.Contains("about", slugs);
        Assert.Contains("sponsors", slugs);
    }

    [Fact]
    public async Task Restore_keeps_place_and_code_page_links_by_slug()
    {
        await BootstrapFirstBootAsync();
        var versionId = await ReadNewestVersionIdAsync();

        long placeId, codeId, oldAboutId;
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var q = new NpgsqlCommand("select id from page where slug = 'about';", conn))
                oldAboutId = (long)(await q.ExecuteScalarAsync())!;
            await using (var p = new NpgsqlCommand(
                "insert into place (name, opens_page_id, created_by) values ('Restore link place', $1, 'test') returning id;", conn))
            {
                p.Parameters.AddWithValue(oldAboutId);
                placeId = (long)(await p.ExecuteScalarAsync())!;
            }
            await using (var c = new NpgsqlCommand(
                "insert into qr_code (tag, batch_no, opens_page_id, created_by) values ('qr-restore-link', 1, (select id from page where slug = 'sponsors'), 'test') returning id;", conn))
                codeId = (long)(await c.ExecuteScalarAsync())!;
        }

        using var req = _host!.EditorRequest(HttpMethod.Post, $"/admin/content/versions/{versionId}/restore");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var q = new NpgsqlCommand(@"
select (select pg.slug from place pl join page pg on pg.id = pl.opens_page_id where pl.id = $1),
       (select pg.slug from qr_code c join page pg on pg.id = c.opens_page_id where c.id = $2),
       (select id from page where slug = 'about');", conn);
            q.Parameters.AddWithValue(placeId);
            q.Parameters.AddWithValue(codeId);
            await using var r = await q.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal("about", r.GetString(0));
            Assert.Equal("sponsors", r.GetString(1));
            // The pages really were recreated: the link follows the slug, not the id.
            Assert.NotEqual(oldAboutId, r.GetInt64(2));
        }
    }

    // -------------------- preview token --------------------

    [Fact]
    public async Task Preview_token_expires_after_the_stored_deadline()
    {
        // Mint through the endpoint.
        using var mint = _host!.EditorRequest(HttpMethod.Post, "/admin/content/preview-token");
        var mintResponse = await _host.Client.SendAsync(mint);
        Assert.Equal(HttpStatusCode.Created, mintResponse.StatusCode);
        var minted = JsonDocument.Parse(await mintResponse.Content.ReadAsStringAsync());
        var token = minted.RootElement.GetProperty("token").GetString()!;
        Assert.StartsWith("wpv_", token);

        // Reuse: the same token resolves until expiry.
        var okResponse = await _host.Client.GetAsync($"/preview/document?token={token}");
        Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);

        // Force expiry by rewriting the row's expires_at into the past.
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var upd = new NpgsqlCommand(
                "update preview_token set expires_at = now() - interval '1 minute';", conn);
            await upd.ExecuteNonQueryAsync();
        }
        var expiredResponse = await _host.Client.GetAsync($"/preview/document?token={token}");
        Assert.Equal(HttpStatusCode.NotFound, expiredResponse.StatusCode);
        Assert.Equal(ApiErrorCodes.PreviewTokenInvalid, await ReadCodeAsync(expiredResponse));
    }

    [Fact]
    public async Task Preview_document_rejects_unknown_token()
    {
        var response = await _host!.Client.GetAsync("/preview/document?token=wpv_not_a_real_token");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -------------------- first boot --------------------

    [Fact]
    public async Task First_boot_on_empty_database_ends_with_version_1_and_snapshot_1()
    {
        // The A14Host constructor already seeded starter content, but nothing
        // publishes or inserts snapshot v1 until SnapshotBootstrap runs. Use a
        // fresh database (independent of the fixture used by other tests) and
        // wire up the same components Program.cs does, then call
        // SnapshotBootstrap.EnsureVersionOneAsync explicitly.
        using var fresh = new FreshDatabase(_fixture);
        var connString = await fresh.CreateAsync();

        // Wire the bootstrap under the same options and objects the fleet uses.
        var options = MakeOptions(connString);
        var connections = TestConnections.For(connString);

        // Migrate to the empty database as the migrate role so the tables land
        // owned by that role and the default privileges of sql.md 12 give the
        // application role select, insert, update, delete on them.
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(connections.Migrate)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(contextOptions))
        {
            await db.Database.MigrateAsync();
        }
        var store = new RecordingObjectStore();
        var gateway = new FakeGatewayClient();
        var iconLibrary = IconLibrary.Load(TestPaths.IconsDir, options.CdnBaseUrl);
        var starter = new StarterContent(NullLogger<StarterContent>.Instance);
        var registry = A14Host.SharedRegistry;
        var validator = A14Host.SharedValidator;
        var docBuilder = new DocumentBuilder();
        var snapshotBuilder = new SnapshotBuilder(store, iconLibrary, options, NullLogger<SnapshotBuilder>.Instance);
        var state = new NodeStateService(connections, NullLogger<NodeStateService>.Instance);
        var writer = new LiveObjectWriter(store, gateway, state, connections, options, new NodeCounters(), NullLogger<LiveObjectWriter>.Instance);
        var publisher = new Publisher(connections, docBuilder, validator, iconLibrary,
            snapshotBuilder, writer, state, NullLogger<Publisher>.Instance);
        var bootstrap = new SnapshotBootstrap(snapshotBuilder, connections, options,
            NullLogger<SnapshotBootstrap>.Instance, starter, publisher);

        // First-boot run under the migration lock, then simulate the fleet
        // writing the live object from state (api.md 10.2, done outside the
        // migration lock by the node after ready flips on).
        await bootstrap.EnsureVersionOneAsync(default);
        await state.RefreshAsync("boot", default);
        await writer.WriteFromStateAsync("boot", default);

        // Version 1.
        Assert.Equal(1L, await ReadNewestVersionIdAsyncFor(connString));

        // Snapshot version 1 with a canonical snapshots/{sha}.json key.
        var (version, key) = await ReadSnapshotRowAsyncFor(connString);
        Assert.Equal(1L, version);
        Assert.StartsWith("snapshots/", key);

        // The live object landed in the store; bytes equal what the writer produced
        // and what the gateway saw.
        var live = await store.GetObjectAsync("live/location.json");
        Assert.NotNull(live);
        Assert.NotNull(writer.LastWrittenBytes);
        Assert.Equal(writer.LastWrittenBytes, live!.Bytes);
        Assert.NotNull(gateway.LastPublished);
        Assert.Equal(writer.LastWrittenBytes, gateway.LastPublished);
    }

    // -------------------- helpers --------------------

    // Bootstraps the first-boot state on the class-scoped fixture (starter
    // content is seeded in InitializeAsync; here we run Publisher.EnsureVersionOneAsync
    // and then insert snapshot v1 so the publish frame's snapshot row lock has a
    // row to grab).
    private async Task BootstrapFirstBootAsync()
    {
        var publisher = _host!.GetService<Publisher>();
        var snapshotBuilder = _host!.GetService<SnapshotBuilder>();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await publisher.EnsureVersionOneAsync(conn, default);

        // Insert snapshot v1 if missing (SnapshotBootstrap step 4 equivalent).
        bool exists;
        await using (var check = new NpgsqlCommand("select 1 from snapshot where id = 1;", conn))
        {
            exists = (await check.ExecuteScalarAsync()) is not null;
        }
        if (!exists)
        {
            await using var tx = await conn.BeginTransactionAsync();
            var built = await snapshotBuilder.BuildAndPutAsync(conn, tx, default);
            await using var ins = new NpgsqlCommand(@"
insert into snapshot (id, version, url, s3_key, built_at) values (1, 1, $1, $2, now());", conn, tx);
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = built.Url });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = built.Key });
            await ins.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }
    }

    // Runs the real ticket -> PUT -> confirm pipeline on the class-scoped host
    // for a raster asset. Returns the media id as its string form (the key the
    // snapshot map uses).
    private async Task<string> UploadAndConfirmRasterAsync(string filename, int width, int height)
    {
        var bytes = BuildPng(width, height);
        return await UploadAndConfirmAsync(filename, "image/png", bytes);
    }

    private async Task<string> UploadAndConfirmSvgAsync(string filename)
    {
        var svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><circle cx=\"12\" cy=\"12\" r=\"5\"/></svg>");
        return await UploadAndConfirmAsync(filename, "image/svg+xml", svg);
    }

    private async Task<string> UploadAndConfirmAsync(string filename, string contentType, byte[] bytes)
    {
        var body = new StringContent(
            $"{{\"filename\":\"{filename}\",\"contentType\":\"{contentType}\",\"sizeBytes\":{bytes.LongLength},\"alt\":\"\",\"title\":\"\"}}",
            Encoding.UTF8, "application/json");
        using var ticketReq = _host!.EditorRequest(HttpMethod.Post, "/admin/media/upload-url");
        ticketReq.Content = body;
        var ticketResp = await _host.Client.SendAsync(ticketReq);
        Assert.Equal(HttpStatusCode.Created, ticketResp.StatusCode);
        using var ticket = JsonDocument.Parse(await ticketResp.Content.ReadAsStringAsync());
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        var uploadUrl = ticket.RootElement.GetProperty("uploadUrl").GetString()!;

        // The presigned URL points at PublicApiBaseUrl (a fake host); route the
        // PUT to the test host by using just the path+query.
        var uri = new Uri(uploadUrl);
        using var put = new HttpRequestMessage(HttpMethod.Put, uri.PathAndQuery)
        {
            Content = new ByteArrayContent(bytes),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        put.Headers.Add("x-amz-tagging", "state=pending");
        var putResp = await _host.Client.SendAsync(put);
        Assert.Equal(HttpStatusCode.NoContent, putResp.StatusCode);

        using var confirmReq = _host.EditorRequest(HttpMethod.Post, $"/admin/media/{id}/confirm");
        var confirmResp = await _host.Client.SendAsync(confirmReq);
        Assert.Equal(HttpStatusCode.OK, confirmResp.StatusCode);
        return id;
    }

    private static byte[] BuildPng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 50, 50, 255));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    // Adds a media block that references the given media asset to the about
    // page's rich_text section (starter content). The block's MediaRef is what
    // the reference checker and the CollectReferencedMediaIds walker pick up.
    private async Task AddMediaBlockToAboutPageAsync(string mediaId)
    {
        var newData = "{\"blocks\":[" +
            "{\"kind\":\"heading\",\"level\":2,\"text\":\"About the flyover\",\"icon\":null}," +
            "{\"kind\":\"paragraph\",\"text\":\"A volunteer helicopter crew flies over the Bitterroot Valley every December so kids can wave at Santa. This is the tracker.\"}," +
            "{\"kind\":\"paragraph\",\"text\":\"The crew flies at their own expense. Sponsors keep the fuel tank full.\"}," +
            "{\"kind\":\"media\",\"media\":{\"mediaId\":\"" + mediaId + "\",\"alt\":\"a hero photo\"},\"caption\":null,\"size\":\"medium\"}" +
            "]}";
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
update section set data = $1::jsonb
where kind = 'rich_text'
  and page_id = (select id from page where slug = 'about');", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = newData });
        var rows = await cmd.ExecuteNonQueryAsync();
        Assert.Equal(1, rows);
    }

    private async Task SeedCurrentEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, 1, true, 'seed', now());", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedSponsorWithLogoAsync(string name, string logoMediaId, int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        long sponsorId;
        await using (var ins = new NpgsqlCommand(
            "insert into sponsor (name, logo_media_id, updated_at) values ($1, $2, now()) returning id;", conn))
        {
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.Parse(logoMediaId) });
            sponsorId = Convert.ToInt64(await ins.ExecuteScalarAsync());
        }
        await using var year_ins = new NpgsqlCommand(@"
insert into sponsor_year (sponsor_id, event_year, amount_donated, active, can_advertise, anonymous)
values ($1, $2, 500, true, true, false);", conn);
        year_ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = sponsorId });
        year_ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        await year_ins.ExecuteNonQueryAsync();
    }

    // Rewrites every hero section's data to a full, publish-valid document
    // whose icon is a media source pointing at the given media id.
    private async Task SetHeroIconMediaAsync(string mediaId)
    {
        var newData = "{\"title\":\"Hero\",\"tagline\":null,\"icon\":{\"source\":\"media\",\"id\":\""
            + mediaId + "\"},\"links\":[],\"height\":\"short\"}";
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "update section set data = $1::jsonb where kind = 'hero';", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = newData });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedCookieTypeWithMediaIconAsync(string name, string mediaId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        var iconJson = "{\"source\":\"media\",\"id\":\"" + mediaId + "\"}";
        await using var cmd = new NpgsqlCommand(@"
insert into cookie_type (name, icon, sort, active, updated_at)
values ($1, $2::jsonb, 0, true, now());", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = iconJson });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string[]> ReadNewestVersionMediaIdsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select media_ids from content_version order by id desc limit 1;", conn);
        var value = await cmd.ExecuteScalarAsync();
        if (value is null || value is DBNull) return Array.Empty<string>();
        var ids = (Guid[])value;
        return ids.Select(g => g.ToString()).ToArray();
    }

    private async Task BumpSiteSettingsSiteNameAsync(string siteName)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
update site_setting_draft set data = jsonb_set(data, '{siteName}', to_jsonb($1::text)),
    updated_by = 'test', updated_at = now() where id = 1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = siteName });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(long MinId, int Count)> ReadVersionCountAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select coalesce(min(id), 0), count(*) from content_version;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), (int)reader.GetInt64(1));
    }

    private async Task<long> ReadNewestVersionIdAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select max(id) from content_version;", conn);
        var r = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(r);
    }

    private static async Task<long> ReadNewestVersionIdAsyncFor(string connString)
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select max(id) from content_version;", conn);
        var r = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(r);
    }

    private static async Task<(long Version, string S3Key)> ReadSnapshotRowAsyncFor(string connString)
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select version, s3_key from snapshot where id = 1;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetString(1));
    }

    // Reads the site settings draft, applies the change, and PUTs it back.
    private async Task<HttpResponseMessage> PutSiteSettingsAsync(Action<JsonObject> change)
    {
        using var getReq = _host!.EditorRequest(HttpMethod.Get, "/admin/site-settings");
        var get = await _host.Client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = JsonNode.Parse(await get.Content.ReadAsStringAsync())!.AsObject();
        var data = body["data"]!.AsObject();
        change(data);
        using var putReq = _host.EditorRequest(HttpMethod.Put, "/admin/site-settings");
        putReq.Content = new StringContent(
            new JsonObject { ["data"] = data.DeepClone() }.ToJsonString(), Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(putReq);
    }

    // Requests an upload ticket and stops there, so the asset stays pending.
    private async Task<string> CreatePendingMediaAsync(string filename)
    {
        using var ticketReq = _host!.EditorRequest(HttpMethod.Post, "/admin/media/upload-url");
        ticketReq.Content = new StringContent(
            $"{{\"filename\":\"{filename}\",\"contentType\":\"image/png\",\"sizeBytes\":1024,\"alt\":\"\",\"title\":\"\"}}",
            Encoding.UTF8, "application/json");
        var ticketResp = await _host.Client.SendAsync(ticketReq);
        Assert.Equal(HttpStatusCode.Created, ticketResp.StatusCode);
        using var ticket = JsonDocument.Parse(await ticketResp.Content.ReadAsStringAsync());
        return ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
    }

    private async Task<(long PageId, long SectionId)> FindSectionAsync(string slug, string kind)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
select p.id, s.id from section s join page p on p.id = s.page_id
where p.slug = $1 and s.kind = $2 order by s.position limit 1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = slug });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = kind });
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"no {kind} section on page {slug}");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<HttpResponseMessage> PatchSectionAsync(long sectionId, string json)
    {
        using var req = _host!.EditorRequest(HttpMethod.Patch, $"/admin/sections/{sectionId}");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    // The presentation of the given section in a published content document.
    private static JsonElement FindPublishedPresentation(JsonElement document, long sectionId) =>
        document.GetProperty("pages").EnumerateArray()
            .SelectMany(p => p.GetProperty("sections").EnumerateArray())
            .Single(s => s.GetProperty("id").GetInt64() == sectionId)
            .GetProperty("presentation");

    private async Task<(string Sha, string Json)> ReadNewestVersionAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select sha256, document::text from content_version order by id desc limit 1;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0).Trim(), reader.GetString(1));
    }

    private async Task<byte[]?> GetLatestSnapshotBytesAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select s3_key from snapshot where id = 1;", conn);
        var key = (string?)await cmd.ExecuteScalarAsync();
        if (key is null) return null;
        var obj = await _host!.Store.GetObjectAsync(key);
        return obj?.Bytes;
    }

    private async Task<int> ReadPageCountAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from page;", conn);
        return (int)Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<HashSet<string>> ReadRolesAsync()
    {
        var roles = new HashSet<string>();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select role from page;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) roles.Add(reader.GetString(0));
        return roles;
    }

    private async Task<HashSet<string>> ReadSlugsAsync()
    {
        var slugs = new HashSet<string>();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select slug from page;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) slugs.Add(reader.GetString(0));
        return slugs;
    }

    private static string HexFor(int seed)
    {
        var sb = new StringBuilder(64);
        var random = new Random(seed);
        for (var i = 0; i < 64; i++) sb.Append("0123456789abcdef"[random.Next(16)]);
        return sb.ToString();
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }

    private static WmsfoOptions MakeOptions(string connString) => new()
    {
        Env = "dev",
        ServiceName = "wmsfo-api-test",
        DbConnection = connString,
        DbMigrationConnection = connString,
        AwsRegion = "us-east-2",
        S3Bucket = "wmsfo-test",
        CdnBaseUrl = "https://cdn.example",
        PublicApiBaseUrl = "https://api.example.com",
        SiteBaseUrl = "https://site.example.com",
        HubUrl = "wss://gateway.example.com/hub",
        GatewayInternalUrl = "http://127.0.0.1:1",
        CorsOrigins = "https://site.example.com",
        TrustedProxyHops = 2,
        CognitoIssuer = "https://cognito-idp.us-east-2.amazonaws.com/us-east-2_pool",
        CognitoClientIds = "site-client-id",
        CognitoUserPoolId = "us-east-2_pool",
        AdminGroup = "admin",
        EditorGroup = "editor",
        SesFromAddress = "alerts@example.com",
        ContactNotifyEmail = "inbox@example.com",
        AlertSendPerSec = 10,
        EnrollmentEncryptionKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
        ReconcileTickMs = 1000,
        LogLevel = "Warning",
    };
}

// Creates an additional test database on the same server for isolation. The
// database gets its own migrate and application roles per sql.md 12 and
// registers with `TestConnections` so `TestConnections.For` maps back the
// same way the fixture's own database does.
internal sealed class FreshDatabase : IDisposable
{
    private readonly PostgresFixture _fixture;
    private string _databaseName = "";
    private string _connString = "";
    private string _migrateRole = "";
    private string _appRole = "";

    public FreshDatabase(PostgresFixture fixture) { _fixture = fixture; }

    public async Task<string> CreateAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        _databaseName = "wmsfo_a14_" + suffix;
        _migrateRole = "wmsfo_t_migrate_" + suffix;
        _appRole = "wmsfo_t_app_" + suffix;

        var migratePassword = RandomPassword();
        var appPassword = RandomPassword();

        var adminBuilder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "postgres" };
        await using (var conn = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await conn.OpenAsync();
            await Exec(conn, $"create database \"{_databaseName}\"");
            await Exec(conn, $"create role \"{_migrateRole}\" login password {Literal(migratePassword)} nosuperuser nocreatedb nocreaterole noinherit");
            await Exec(conn, $"create role \"{_appRole}\" login password {Literal(appPassword)} nosuperuser nocreatedb nocreaterole noinherit");
            await Exec(conn, $"alter database \"{_databaseName}\" owner to \"{_migrateRole}\"");
            await Exec(conn, $"revoke all on database \"{_databaseName}\" from public");
            await Exec(conn, $"grant connect on database \"{_databaseName}\" to \"{_migrateRole}\", \"{_appRole}\"");
            await Exec(conn, $"alter role \"{_migrateRole}\" in database \"{_databaseName}\" set timezone = 'UTC'");
            await Exec(conn, $"alter role \"{_appRole}\" in database \"{_databaseName}\" set timezone = 'UTC'");
        }

        var testBuilder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = _databaseName };
        _connString = testBuilder.ConnectionString;

        await using (var dbConn = new NpgsqlConnection(_connString))
        {
            await dbConn.OpenAsync();
            await Exec(dbConn, $"grant usage, create on schema public to \"{_migrateRole}\"");
            await Exec(dbConn, "revoke create on schema public from public");
            await Exec(dbConn, $"grant usage on schema public to \"{_appRole}\"");
            await Exec(dbConn,
                $"alter default privileges for role \"{_migrateRole}\" in schema public " +
                $"grant select, insert, update, delete on tables to \"{_appRole}\"");
        }

        var appConn = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = _databaseName,
            Username = _appRole,
            Password = appPassword,
        }.ConnectionString;
        var migrateConn = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = _databaseName,
            Username = _migrateRole,
            Password = migratePassword,
        }.ConnectionString;
        TestConnections.Register(_databaseName, appConn, migrateConn);
        return _connString;
    }

    public void Dispose()
    {
        if (string.IsNullOrEmpty(_databaseName)) return;
        TestConnections.Unregister(_databaseName);
        try
        {
            NpgsqlConnection.ClearAllPools();
            var b = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "postgres" };
            using var conn = new NpgsqlConnection(b.ConnectionString);
            conn.Open();
            using (var cmd = new NpgsqlCommand($"drop database if exists \"{_databaseName}\" with (force)", conn))
            {
                cmd.ExecuteNonQuery();
            }
            foreach (var role in new[] { _appRole, _migrateRole })
            {
                if (string.IsNullOrEmpty(role)) continue;
                try
                {
                    using var dropRole = new NpgsqlCommand($"drop role if exists \"{role}\"", conn);
                    dropRole.ExecuteNonQuery();
                }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string RandomPassword()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
}
