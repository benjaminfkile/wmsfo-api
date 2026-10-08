using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.IntegrationTests;

// The theme endpoints of contracts 4.5 Themes and api.md 11a.10: the
// list, create with the style, chrome, and overlay rules, patch, sprite
// tickets and confirm, default, impact, and delete with an optional
// replacement; the [snapshot] rule, the audit rows, and the capability.
public sealed class A95ThemeEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Cdn = "https://cdn.example";

    private const string Chrome =
        """{"bg":"#ffffff","fg":"#5f6368","text":"#202124","tile":"#e8f0fe","tileFg":"#1a56c4","panel":"#ffffffe6","accent":"#1a56c4"}""";
    private const string Overlay =
        """{"routeColor":"#1a56c4","routeOpacity":0.9,"arrowColor":"#1a56c4","timeLabelBg":"#ffffff","timeLabelFg":"#202124","timeLabelOpacity":1,"userColor":"#c62828"}""";
    private const string GoogleStyle =
        """[{"featureType":"road","elementType":"geometry","stylers":[{"color":"#a95a95"}]}]""";
    private const string MapLibreStyle =
        """{"version":8,"sources":{"basemap":{"type":"vector"},"terrain":{"type":"raster-dem"}},"layers":[{"id":"bg","type":"background","paint":{"background-color":"#fafafa"}},{"id":"water","type":"fill","source":"basemap","source-layer":"water"}]}""";

    private static readonly string[] SeedOrder =
        ["standard", "expedition", "blizzard", "charcoal", "night", "nebula", "route-light", "route-dark"];

    private readonly PostgresFixture _fixture;
    private A95Host? _host;

    public A95ThemeEndpointsTests(PostgresFixture fixture)
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

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from cookie;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from location;",
            "delete from event;",
            "delete from api_key;",
            "delete from tracker_theme where created_by <> 'seed';",
            "update tracker_theme set default_light_mode = false, default_dark_mode = false;",
            "update tracker_theme set default_light_mode = true where key in ('route-light', 'standard');",
            "update tracker_theme set default_dark_mode = true where key in ('route-dark', 'night');",
            "delete from audit_log where entity = 'tracker_theme';",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
        _host = await A95Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    // ---------- list ----------

    [Fact]
    public async Task The_list_has_the_eight_seeds_in_order_with_their_event_counts()
    {
        var e1 = await InsertEventAsync(2025, "Flight 2025", current: false);
        var e2 = await InsertEventAsync(2026, "Flight 2026", current: true);
        await EnableAsync(e1, await ThemeIdAsync("standard"));
        foreach (var key in SeedOrder) await EnableAsync(e2, await ThemeIdAsync(key));

        using var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/admin/themes", null));
        var items = list.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(SeedOrder, items.Select(i => i.GetProperty("key").GetString()).ToArray());
        foreach (var item in items)
        {
            var id = item.GetProperty("id").GetInt64();
            Assert.Equal(await CountAsync("select count(*)::int from event_tracker_theme where theme_id = $1", id),
                item.GetProperty("eventCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("spriteUrl").ValueKind);
            Assert.Equal(Cdn + "/themes/" + item.GetProperty("styleSha256").GetString() + ".json",
                item.GetProperty("styleUrl").GetString());
            Assert.True(item.TryGetProperty("audit", out _));
        }
        Assert.Equal(2, items[0].GetProperty("eventCount").GetInt32());
    }

    // ---------- create ----------

    [Fact]
    public async Task A_valid_google_and_a_valid_maplibre_theme_read_back_with_the_style_url_of_their_hash()
    {
        foreach (var (renderer, key, style) in new[] { ("google", "a95-google", GoogleStyle), ("maplibre", "a95-maplibre", MapLibreStyle) })
        {
            var response = await SendAsync(HttpMethod.Post, "/admin/themes", Body(renderer, key, style).ToJsonString());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var created = await ReadJsonAsync(response);
            var root = created.RootElement;
            var canonical = ThemeStyles.Canonicalize(JsonDocument.Parse(style).RootElement);
            var sha = CanonicalJson.Sha256Hex(canonical);
            Assert.Equal(sha, root.GetProperty("styleSha256").GetString());
            Assert.Equal(canonical.Length, root.GetProperty("styleBytes").GetInt32());
            Assert.Equal($"{Cdn}/themes/{sha}.json", root.GetProperty("styleUrl").GetString());
            Assert.Equal("create", root.GetProperty("audit").GetProperty("action").GetString());
            Assert.Equal(0, root.GetProperty("eventCount").GetInt32());

            var stored = _host!.Store.Objects[$"themes/{sha}.json"];
            Assert.Equal(canonical, stored.Bytes);
            Assert.Equal("public, max-age=31536000, immutable", stored.CacheControl);

            var listed = await FindInListAsync(root.GetProperty("id").GetInt64());
            Assert.Equal($"{Cdn}/themes/{sha}.json", listed.GetProperty("styleUrl").GetString());
            Assert.Equal(renderer, listed.GetProperty("renderer").GetString());
            Assert.Equal("#202124", listed.GetProperty("chrome").GetProperty("text").GetString());
            Assert.Equal(0.9, listed.GetProperty("overlay").GetProperty("routeOpacity").GetDouble());
        }
    }

    [Fact]
    public async Task A_multipart_create_reads_style_from_its_file_part_and_an_unparsable_part_is_400_at_style()
    {
        var ok = await SendMultipartAsync("a95-form", Encoding.UTF8.GetBytes(MapLibreStyle));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        using (var created = await ReadJsonAsync(ok))
        {
            Assert.Equal("maplibre", created.RootElement.GetProperty("renderer").GetString());
            Assert.Equal(7, created.RootElement.GetProperty("sortOrder").GetInt32());
        }

        var bad = await SendMultipartAsync("a95-form-bad", Encoding.UTF8.GetBytes("{\"version\":8,"));
        await AssertValidationAsync(bad, "style");
    }

    public static IEnumerable<object[]> RefusedStyles()
    {
        yield return ["google", """[{"featureType":"road","stylers":[],"colour":"red"}]""", "only featureType, elementType, and stylers"];
        yield return ["maplibre", MapLibreStyle.Replace("\"version\":8", "\"version\":7"), "version must be 8"];
        yield return ["maplibre", MapLibreStyle.Replace("\"terrain\":{\"type\":\"raster-dem\"}", "\"osm\":{\"type\":\"vector\"}"), "source `osm` is not allowed"];
        yield return ["maplibre", MapLibreStyle.Replace("{\"type\":\"vector\"}", "{\"type\":\"vector\",\"url\":\"pmtiles://valley\"}"), "must not carry url"];
        yield return ["maplibre", MapLibreStyle.Replace("\"source\":\"basemap\"", "\"source\":\"osm\""), "must name source basemap or terrain"];
        yield return ["maplibre", MapLibreStyle.Replace("\"source-layer\":\"water\"", "\"source-layer\":\"water\",\"metadata\":{\"link\":\"https://tiles.example\"}"), "starts with http"];
        yield return ["google", OverCapGoogleStyle(), "at most 65536 bytes"];
    }

    [Theory]
    [MemberData(nameof(RefusedStyles))]
    public async Task Each_style_rule_is_refused_at_style_with_its_reason(string renderer, string style, string reason)
    {
        var response = await SendAsync(HttpMethod.Post, "/admin/themes", Body(renderer, "a95-refused", style).ToJsonString());
        var message = await AssertValidationAsync(response, "style");
        Assert.Contains(reason, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chrome_and_overlay_rules_are_refused_at_their_paths()
    {
        var lowText = Body("google", "a95-contrast", GoogleStyle);
        lowText["chrome"]!["text"] = "#eeeeee";
        await AssertValidationAsync(await SendAsync(HttpMethod.Post, "/admin/themes", lowText.ToJsonString()), "chrome.text");

        var lowTile = Body("google", "a95-contrast", GoogleStyle);
        lowTile["chrome"]!["tileFg"] = "#e8f0fe";
        await AssertValidationAsync(await SendAsync(HttpMethod.Post, "/admin/themes", lowTile.ToJsonString()), "chrome.tileFg");

        var opacity = Body("google", "a95-contrast", GoogleStyle);
        opacity["overlay"]!["routeOpacity"] = 1.5;
        await AssertValidationAsync(await SendAsync(HttpMethod.Post, "/admin/themes", opacity.ToJsonString()), "overlay.routeOpacity");

        var missing = Body("google", "a95-contrast", GoogleStyle);
        missing["chrome"]!.AsObject().Remove("panel");
        await AssertValidationAsync(await SendAsync(HttpMethod.Post, "/admin/themes", missing.ToJsonString()), "chrome.panel");

        Assert.Equal(0, await CountAsync("select count(*)::int from tracker_theme where key = $1::text", "a95-contrast"));
    }

    [Fact]
    public async Task A_duplicate_key_on_the_renderer_is_400_at_key_and_the_other_renderer_takes_it()
    {
        await SendAsync(HttpMethod.Post, "/admin/themes", Body("google", "a95-dup", GoogleStyle).ToJsonString());
        var dup = await SendAsync(HttpMethod.Post, "/admin/themes", Body("google", "a95-dup", GoogleStyle).ToJsonString());
        await AssertValidationAsync(dup, "key");
        var seedKey = await SendAsync(HttpMethod.Post, "/admin/themes", Body("google", "standard", GoogleStyle).ToJsonString());
        await AssertValidationAsync(seedKey, "key");

        var other = await SendAsync(HttpMethod.Post, "/admin/themes", Body("maplibre", "a95-dup", MapLibreStyle).ToJsonString());
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task The_style_object_is_written_once()
    {
        var style = GoogleStyle.Replace("#a95a95", "#0a9501");
        var sha = CanonicalJson.Sha256Hex(ThemeStyles.Canonicalize(JsonDocument.Parse(style).RootElement));
        var key = $"themes/{sha}.json";
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/themes", Body("google", "a95-once-a", style).ToJsonString())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/themes", Body("google", "a95-once-b", style).ToJsonString())).StatusCode);
        lock (_host!.Store.PutKeys) Assert.Equal(1, _host.Store.PutKeys.Count(k => k == key));
    }

    // ---------- patch ----------

    [Fact]
    public async Task A_patched_style_writes_a_new_object_and_leaves_the_old_and_renderer_is_immutable()
    {
        using var created = await CreateAsync("maplibre", "a95-patch", MapLibreStyle);
        var id = Id(created);
        var oldSha = created.RootElement.GetProperty("styleSha256").GetString()!;

        var newStyle = MapLibreStyle.Replace("#fafafa", "#f0f0f0");
        var patch = await SendAsync(HttpMethod.Patch, $"/admin/themes/{id}",
            new JsonObject { ["style"] = JsonNode.Parse(newStyle), ["name"] = "Patched" }.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var patched = await ReadJsonAsync(patch);
        var newSha = patched.RootElement.GetProperty("styleSha256").GetString()!;
        Assert.NotEqual(oldSha, newSha);
        Assert.Equal("Patched", patched.RootElement.GetProperty("name").GetString());
        Assert.Equal("update", patched.RootElement.GetProperty("audit").GetProperty("action").GetString());
        Assert.True(_host!.Store.Objects.ContainsKey($"themes/{oldSha}.json"));
        Assert.True(_host.Store.Objects.ContainsKey($"themes/{newSha}.json"));

        var renderer = await SendAsync(HttpMethod.Patch, $"/admin/themes/{id}", """{"renderer":"google"}""");
        await AssertValidationAsync(renderer, "renderer");

        var key = await SendAsync(HttpMethod.Patch, $"/admin/themes/{id}", """{"key":"a95-patch-renamed"}""");
        Assert.Equal(HttpStatusCode.OK, key.StatusCode);
        using var renamed = await ReadJsonAsync(key);
        Assert.Equal(newSha, renamed.RootElement.GetProperty("styleSha256").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Patch, "/admin/themes/987654321", """{"name":"x"}""")).StatusCode);
    }

    // ---------- sprite ----------

    [Fact]
    public async Task Sprite_tickets_refuse_google_and_bad_hashes_and_answer_four_tickets()
    {
        var google = await ThemeIdAsync("standard");
        var index = new string('b', 64);
        await AssertValidationAsync(await SendAsync(HttpMethod.Post, $"/admin/themes/{google}/sprite",
            $"{{\"indexSha256\":\"{index}\"}}"), "renderer");

        using var created = await CreateAsync("maplibre", "a95-sprite-tickets", MapLibreStyle);
        var id = Id(created);
        await AssertValidationAsync(await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/sprite",
            $"{{\"indexSha256\":\"{index.ToUpperInvariant()}\"}}"), "indexSha256");

        var response = await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/sprite", $"{{\"indexSha256\":\"{index}\"}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var tickets = await ReadJsonAsync(response);
        Assert.Equal(index, tickets.RootElement.GetProperty("indexSha256").GetString());
        var uploads = tickets.RootElement.GetProperty("uploads").EnumerateArray().ToArray();
        Assert.Equal(new[] { "sprite.json", "sprite.png", "sprite@2x.json", "sprite@2x.png" },
            uploads.Select(u => u.GetProperty("file").GetString()!).ToArray());
        foreach (var upload in uploads)
        {
            var file = upload.GetProperty("file").GetString()!;
            Assert.Equal($"https://example/local-upload/themes/{id}/sprites/{index}/{file}", upload.GetProperty("uploadUrl").GetString());
            Assert.Equal("PUT", upload.GetProperty("method").GetString());
            var headers = upload.GetProperty("headers");
            Assert.Equal(file.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "application/json; charset=utf-8",
                headers.GetProperty("Content-Type").GetString());
            Assert.Equal("state=pending", headers.GetProperty("x-amz-tagging").GetString());
            Assert.Equal(2, headers.EnumerateObject().Count());
        }
    }

    [Fact]
    public async Task Sprite_confirm_checks_the_files_and_a_good_set_rewrites_the_style()
    {
        using var created = await CreateAsync("maplibre", "a95-sprite", MapLibreStyle);
        var id = Id(created);
        var oldSha = created.RootElement.GetProperty("styleSha256").GetString()!;
        var (spriteJson, index) = SpriteIndex();

        // A missing file.
        await UploadSpriteAsync(id, index, spriteJson, skip: "sprite@2x.png");
        var missing = await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/sprite/confirm", $"{{\"indexSha256\":\"{index}\"}}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var error = await ReadJsonAsync(missing))
        {
            Assert.Equal("upload_not_found", error.RootElement.GetProperty("code").GetString());
            Assert.Equal("sprite@2x.png", error.RootElement.GetProperty("details").GetProperty("file").GetString());
        }

        // A PNG without the signature: 400 at file and the four deleted.
        await UploadSpriteAsync(id, index, spriteJson, badPng: "sprite.png");
        var badPng = await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/sprite/confirm", $"{{\"indexSha256\":\"{index}\"}}");
        var body = await badPng.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, badPng.StatusCode);
        using (var error = JsonDocument.Parse(body))
        {
            Assert.True(error.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty("file", out _));
            Assert.Equal("sprite.png", error.RootElement.GetProperty("details").GetProperty("file").GetString());
        }
        Assert.All(SpriteKeys(id, index), k => Assert.False(_host!.Store.Objects.ContainsKey(k)));

        // A sprite.json that does not hash to the prefix.
        var wrong = new string('c', 64);
        await UploadSpriteAsync(id, wrong, spriteJson);
        await AssertValidationAsync(await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/sprite/confirm",
            $"{{\"indexSha256\":\"{wrong}\"}}"), "indexSha256");
        Assert.All(SpriteKeys(id, wrong), k => Assert.False(_host!.Store.Objects.ContainsKey(k)));

        // A good set.
        await UploadSpriteAsync(id, index, spriteJson);
        var ok = await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/sprite/confirm", $"{{\"indexSha256\":\"{index}\"}}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var confirmed = await ReadJsonAsync(ok);
        var root = confirmed.RootElement;
        var spriteBase = $"{Cdn}/themes/{id}/sprites/{index}/sprite";
        Assert.Equal(index, root.GetProperty("spriteSha256").GetString());
        Assert.Equal(spriteBase, root.GetProperty("spriteUrl").GetString());
        Assert.Equal("sprite", root.GetProperty("audit").GetProperty("action").GetString());
        var newSha = root.GetProperty("styleSha256").GetString()!;
        Assert.NotEqual(oldSha, newSha);
        Assert.True(_host!.Store.Objects.ContainsKey($"themes/{oldSha}.json"));
        using (var style = JsonDocument.Parse(_host.Store.Objects[$"themes/{newSha}.json"].Bytes))
            Assert.Equal(spriteBase, style.RootElement.GetProperty("sprite").GetString());
        foreach (var key in SpriteKeys(id, index))
        {
            Assert.Null(await _host.Store.GetObjectTaggingAsync(key));
            Assert.Contains(key, _host.Store.UntagCalls);
        }

        // Idempotent on the confirmed set; the tickets answer 200 for it.
        var again = await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/sprite/confirm", $"{{\"indexSha256\":\"{index}\"}}");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using (var same = await ReadJsonAsync(again))
            Assert.Equal(newSha, same.RootElement.GetProperty("styleSha256").GetString());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/sprite",
            $"{{\"indexSha256\":\"{index}\"}}")).StatusCode);

        // A later style patch keeps the sprite base.
        var patch = await SendAsync(HttpMethod.Patch, $"/admin/themes/{id}",
            new JsonObject { ["style"] = JsonNode.Parse(MapLibreStyle.Replace("#fafafa", "#fbfbfb")) }.ToJsonString());
        using var patched = await ReadJsonAsync(patch);
        using (var style = JsonDocument.Parse(_host.Store.Objects[$"themes/{patched.RootElement.GetProperty("styleSha256").GetString()}.json"].Bytes))
            Assert.Equal(spriteBase, style.RootElement.GetProperty("sprite").GetString());
    }

    // ---------- default ----------

    [Fact]
    public async Task Default_light_moves_the_flag_off_the_previous_holder()
    {
        using var created = await CreateAsync("maplibre", "a95-default", MapLibreStyle);
        var id = Id(created);
        var routeLight = await ThemeIdAsync("route-light");

        var response = await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/default", """{"light":true}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var dto = await ReadJsonAsync(response))
        {
            Assert.True(dto.RootElement.GetProperty("defaultLightMode").GetBoolean());
            Assert.False(dto.RootElement.GetProperty("defaultDarkMode").GetBoolean());
            Assert.Equal("default", dto.RootElement.GetProperty("audit").GetProperty("action").GetString());
        }
        Assert.False(await ScalarAsync<bool>($"select default_light_mode from tracker_theme where id = {routeLight}"));
        Assert.Equal(1, await CountAsync("select count(*)::int from tracker_theme where renderer = $1::text and default_light_mode", "maplibre"));
        Assert.True(await ScalarAsync<bool>($"select default_dark_mode from tracker_theme where key = 'route-dark'"));

        // Both flags on one theme, then false clears it on this theme.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/default", """{"dark":true}""")).StatusCode);
        Assert.Equal(1, await CountAsync("select count(*)::int from tracker_theme where renderer = $1::text and default_dark_mode", "maplibre"));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/default", """{"light":false}""")).StatusCode);
        Assert.Equal(0, await CountAsync("select count(*)::int from tracker_theme where renderer = $1::text and default_light_mode", "maplibre"));
        Assert.True(await ScalarAsync<bool>($"select default_dark_mode from tracker_theme where id = {id}"));
    }

    // ---------- impact and delete ----------

    [Fact]
    public async Task The_impact_lists_the_enabling_events_names_the_flag_and_blocks_the_only_google_theme()
    {
        using var created = await CreateAsync("google", "a95-impact", GoogleStyle);
        var id = Id(created);
        var older = await InsertEventAsync(2025, "Flight 2025", current: false);
        var newer = await InsertEventAsync(2026, "Flight 2026", current: false);
        await EnableAsync(older, id);
        await EnableAsync(older, await ThemeIdAsync("standard"));
        await EnableAsync(newer, id);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/default", """{"dark":true}""")).StatusCode);

        using var impact = await ReadJsonAsync(await SendAsync(HttpMethod.Get, $"/admin/themes/{id}/impact", null));
        var root = impact.RootElement;
        var group = Assert.Single(root.GetProperty("unlinks").EnumerateArray());
        Assert.Equal("event", group.GetProperty("entity").GetString());
        Assert.Equal(2, group.GetProperty("count").GetInt32());
        Assert.Equal(new[] { "Flight 2025", "Flight 2026" }, group.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray());
        Assert.Equal(new[] { "The google renderer loses its default dark theme." },
            root.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray());
        Assert.Equal("This is the only Google theme enabled on Flight 2026. Enable another there first.",
            root.GetProperty("blocked").GetString());

        using var free = await ReadJsonAsync(await SendAsync(HttpMethod.Get, $"/admin/themes/{await ThemeIdAsync("expedition")}/impact", null));
        Assert.Equal(JsonValueKind.Null, free.RootElement.GetProperty("blocked").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/admin/themes/987654321/impact", null)).StatusCode);
    }

    [Fact]
    public async Task Delete_without_a_replacement_drops_the_join_rows_and_clears_the_flags()
    {
        using var created = await CreateAsync("maplibre", "a95-delete", MapLibreStyle);
        var id = Id(created);
        var styleKey = $"themes/{created.RootElement.GetProperty("styleSha256").GetString()}.json";
        var ev = await InsertEventAsync(2026, "Flight 2026", current: false);
        await EnableAsync(ev, id);
        await EnableAsync(ev, await ThemeIdAsync("standard"));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/default", """{"light":true}""")).StatusCode);
        var leftover = $"themes/{id}/sprites/{new string('d', 64)}/sprite.png";
        await _host!.Store.PutObjectAsync(leftover, new byte[] { 1 }, "image/png", "no-store", ObjectTags.Pending);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/admin/themes/{id}", null)).StatusCode);
        Assert.Equal(0, await CountAsync("select count(*)::int from event_tracker_theme where theme_id = $1", id));
        Assert.Equal(1, await CountAsync("select count(*)::int from event_tracker_theme where event_id = $1", ev));
        Assert.Equal(0, await CountAsync("select count(*)::int from tracker_theme where renderer = $1::text and default_light_mode", "maplibre"));
        Assert.False(_host.Store.Objects.ContainsKey(leftover));
        Assert.True(_host.Store.Objects.ContainsKey(styleKey));

        var before = await ScalarAsync<string>(
            $"select before::text from audit_log where entity = 'tracker_theme' and entity_id = '{id}' and action = 'delete'");
        using var beforeDoc = JsonDocument.Parse(before);
        Assert.Equal("a95-delete", beforeDoc.RootElement.GetProperty("key").GetString());
        Assert.Equal(1, beforeDoc.RootElement.GetProperty("impact").GetProperty("unlinks")[0].GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/admin/themes/{id}", null)).StatusCode);
    }

    [Fact]
    public async Task Delete_with_a_replacement_reenables_it_and_moves_the_flags()
    {
        using var deleted = await CreateAsync("maplibre", "a95-replaced", MapLibreStyle);
        using var replacement = await CreateAsync("maplibre", "a95-replacement", MapLibreStyle);
        var id = Id(deleted);
        var r = Id(replacement);
        var e1 = await InsertEventAsync(2025, "Flight 2025", current: false);
        var e2 = await InsertEventAsync(2026, "Flight 2026", current: false);
        await EnableAsync(e1, id);
        await EnableAsync(e1, await ThemeIdAsync("standard"));
        await EnableAsync(e2, id);
        await EnableAsync(e2, r);
        await EnableAsync(e2, await ThemeIdAsync("standard"));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/default", """{"light":true,"dark":true}""")).StatusCode);

        var response = await SendAsync(HttpMethod.Delete, $"/admin/themes/{id}", $"{{\"replacementId\":{r}}}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, await CountAsync("select count(*)::int from event_tracker_theme where theme_id = $1", r));
        Assert.True(await ScalarAsync<bool>($"select default_light_mode and default_dark_mode from tracker_theme where id = {r}"));
        Assert.Equal(1, await CountAsync("select count(*)::int from tracker_theme where renderer = $1::text and default_light_mode", "maplibre"));
    }

    [Fact]
    public async Task Deleting_the_only_google_theme_of_an_event_needs_a_google_replacement()
    {
        using var only = await CreateAsync("google", "a95-only", GoogleStyle);
        using var other = await CreateAsync("google", "a95-other", GoogleStyle);
        using var maplibre = await CreateAsync("maplibre", "a95-ml", MapLibreStyle);
        var id = Id(only);
        var ev = await InsertEventAsync(2026, "Flight 2026", current: false);
        await EnableAsync(ev, id);
        await EnableAsync(ev, Id(maplibre));

        var refused = await SendAsync(HttpMethod.Delete, $"/admin/themes/{id}", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using (var error = await ReadJsonAsync(refused))
            Assert.Equal("last_google_theme", error.RootElement.GetProperty("code").GetString());

        await AssertValidationAsync(await SendAsync(HttpMethod.Delete, $"/admin/themes/{id}",
            $"{{\"replacementId\":{Id(maplibre)}}}"), "replacementId");
        await AssertValidationAsync(await SendAsync(HttpMethod.Delete, $"/admin/themes/{id}",
            $"{{\"replacementId\":{id}}}"), "replacementId");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/admin/themes/{id}",
            "{\"replacementId\":987654321}")).StatusCode);
        Assert.Equal(1, await CountAsync("select count(*)::int from tracker_theme where id = $1", id));

        var lifted = await SendAsync(HttpMethod.Delete, $"/admin/themes/{id}", $"{{\"replacementId\":{Id(other)}}}");
        Assert.Equal(HttpStatusCode.NoContent, lifted.StatusCode);
        Assert.Equal(1, await CountAsync(
            $"select count(*)::int from event_tracker_theme where event_id = $1 and theme_id = {Id(other)}", ev));
    }

    // ---------- the [snapshot] rule ----------

    [Fact]
    public async Task A_write_on_a_theme_the_current_event_enables_bumps_the_snapshot_and_one_it_does_not_leaves_it()
    {
        using var enabled = await CreateAsync("google", "a95-snap-on", GoogleStyle);
        using var disabled = await CreateAsync("google", "a95-snap-off", GoogleStyle);
        var ev = await InsertEventAsync(2026, "Flight 2026", current: true);
        await EnableAsync(ev, Id(enabled));

        var v0 = await SnapshotVersionAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Patch, $"/admin/themes/{Id(disabled)}", """{"name":"Off"}""")).StatusCode);
        Assert.Equal(v0, await SnapshotVersionAsync());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Patch, $"/admin/themes/{Id(enabled)}", """{"name":"On"}""")).StatusCode);
        Assert.Equal(v0 + 1, await SnapshotVersionAsync());

        // The flag's previous holder counts: standard holds google light and
        // is not enabled; the disabled theme takes it without a rebuild.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/themes/{Id(disabled)}/default", """{"light":true}""")).StatusCode);
        Assert.Equal(v0 + 1, await SnapshotVersionAsync());
        // The enabled theme takes it from the disabled one: a rebuild.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/themes/{Id(enabled)}/default", """{"light":true}""")).StatusCode);
        Assert.Equal(v0 + 2, await SnapshotVersionAsync());
        // The disabled theme takes it back from the enabled holder: a rebuild.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/themes/{Id(disabled)}/default", """{"light":true}""")).StatusCode);
        Assert.Equal(v0 + 3, await SnapshotVersionAsync());
    }

    // ---------- audit ----------

    [Fact]
    public async Task Every_theme_write_records_its_audit_row()
    {
        async Task<int> Rows() => await CountAsync("select count(*)::int from audit_log where entity = $1::text", "tracker_theme");
        var start = await Rows();

        using var created = await CreateAsync("maplibre", "a95-audit", MapLibreStyle);
        var id = Id(created);
        Assert.Equal(start + 1, await Rows());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Patch, $"/admin/themes/{id}", """{"sortOrder":3}""")).StatusCode);
        Assert.Equal(start + 2, await Rows());
        var (spriteJson, index) = SpriteIndex();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/sprite", $"{{\"indexSha256\":\"{index}\"}}")).StatusCode);
        Assert.Equal(start + 3, await Rows());
        await UploadSpriteAsync(id, index, spriteJson);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/sprite/confirm", $"{{\"indexSha256\":\"{index}\"}}")).StatusCode);
        Assert.Equal(start + 4, await Rows());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/themes/{id}/default", """{"dark":true}""")).StatusCode);
        Assert.Equal(start + 5, await Rows());
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/admin/themes/{id}", null)).StatusCode);
        Assert.Equal(start + 6, await Rows());

        var actions = new List<string>();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select action, actor from audit_log where entity = 'tracker_theme' and entity_id = $1 order by id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(0));
            Assert.Equal("person:" + DevStaticTokens.AdminEmail, reader.GetString(1));
        }
        Assert.Equal(["create", "update", "sprite", "sprite", "default", "delete"], actions);
    }

    // ---------- capability ----------

    [Fact]
    public async Task An_api_key_with_themes_reaches_the_list_and_one_without_is_403()
    {
        var with = await MintKeyAsync("a95-themes", "themes");
        var without = await MintKeyAsync("a95-events", "events");
        using (var req = KeyRequest(HttpMethod.Get, "/admin/themes", with))
            Assert.Equal(HttpStatusCode.OK, (await _host!.Client.SendAsync(req)).StatusCode);
        using (var req = KeyRequest(HttpMethod.Get, "/admin/themes", without))
            Assert.Equal(HttpStatusCode.Forbidden, (await _host!.Client.SendAsync(req)).StatusCode);
        using (var req = KeyRequest(HttpMethod.Get, "/admin/themes/1/impact", without))
            Assert.Equal(HttpStatusCode.Forbidden, (await _host!.Client.SendAsync(req)).StatusCode);
    }

    // ---------- helpers ----------

    private static JsonObject Body(string renderer, string key, string style) => new()
    {
        ["renderer"] = renderer,
        ["key"] = key,
        ["name"] = "Theme " + key,
        ["sortOrder"] = 90,
        ["style"] = JsonNode.Parse(style),
        ["chrome"] = JsonNode.Parse(Chrome),
        ["overlay"] = JsonNode.Parse(Overlay),
        ["thumbnailMediaId"] = null,
    };

    private static string OverCapGoogleStyle()
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < 1300; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("""{"featureType":"road","stylers":[{"color":"#123456"}]}""");
        }
        return sb.Append(']').ToString();
    }

    private async Task<JsonDocument> CreateAsync(string renderer, string key, string style)
    {
        var response = await SendAsync(HttpMethod.Post, "/admin/themes", Body(renderer, key, style).ToJsonString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<HttpResponseMessage> SendMultipartAsync(string key, byte[] style)
    {
        using var req = _host!.AdminRequest(HttpMethod.Post, "/admin/themes");
        var form = new MultipartFormDataContent
        {
            { new StringContent("maplibre"), "renderer" },
            { new StringContent(key), "key" },
            { new StringContent("Form theme"), "name" },
            { new StringContent("7"), "sortOrder" },
            { new StringContent(Chrome), "chrome" },
            { new StringContent(Overlay), "overlay" },
        };
        var file = new ByteArrayContent(style);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(file, "style", "style.json");
        req.Content = form;
        return await _host.Client.SendAsync(req);
    }

    private static (byte[] Json, string Index) SpriteIndex()
    {
        var json = Encoding.UTF8.GetBytes("""{ "pin": {"y": 0, "x": 0, "width": 16, "height": 16, "pixelRatio": 1} }""");
        using var doc = JsonDocument.Parse(json);
        return (json, CanonicalJson.Sha256Hex(ThemeStyles.Canonicalize(doc.RootElement)));
    }

    private static IEnumerable<string> SpriteKeys(long id, string index) =>
        new[] { "sprite.json", "sprite.png", "sprite@2x.json", "sprite@2x.png" }
            .Select(f => $"themes/{id}/sprites/{index}/{f}");

    private async Task UploadSpriteAsync(long id, string index, byte[] spriteJson, string? skip = null, string? badPng = null)
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
        foreach (var file in new[] { "sprite.json", "sprite.png", "sprite@2x.json", "sprite@2x.png" })
        {
            var key = $"themes/{id}/sprites/{index}/{file}";
            if (file == skip)
            {
                await _host!.Store.DeleteObjectAsync(key);
                continue;
            }
            var isPng = file.EndsWith(".png", StringComparison.Ordinal);
            var bytes = isPng ? (file == badPng ? "not a png"u8.ToArray() : png) : spriteJson;
            await _host!.Store.PutObjectAsync(key, bytes, isPng ? "image/png" : "application/json; charset=utf-8",
                "public, max-age=31536000, immutable", ObjectTags.Pending);
        }
    }

    private async Task<string> MintKeyAsync(string name, string capability)
    {
        var response = await SendAsync(HttpMethod.Post, "/admin/api-keys",
            $"{{\"name\":\"{name}\",\"allCapabilities\":false,\"capabilities\":[\"{capability}\"]}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("key").GetString()!;
    }

    private static HttpRequestMessage KeyRequest(HttpMethod method, string path, string key)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body)
    {
        using var req = _host!.AdminRequest(method, path);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task<JsonElement> FindInListAsync(long id)
    {
        using var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/admin/themes", null));
        return list.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetInt64() == id).Clone();
    }

    // Asserts 400 validation_failed with the field in details.fields; answers
    // the field's reason.
    private static async Task<string> AssertValidationAsync(HttpResponseMessage response, string field)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 at {field}, got {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("validation_failed", doc.RootElement.GetProperty("code").GetString());
        var fields = doc.RootElement.GetProperty("details").GetProperty("fields");
        Assert.True(fields.TryGetProperty(field, out var reason), $"expected a failure at {field}: {text}");
        return reason.GetString()!;
    }

    private static long Id(JsonDocument doc) => doc.RootElement.GetProperty("id").GetInt64();

    private async Task<long> InsertEventAsync(int year, string name, bool current)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, 1, $3, 'a95-test', now())
returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = current });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task EnableAsync(long eventId, long themeId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "insert into event_tracker_theme (event_id, theme_id) values ($1, $2) on conflict do nothing;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = themeId });
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<long> ThemeIdAsync(string key) =>
        ScalarAsync<long>($"select id from tracker_theme where key = '{key}'");

    private Task<long> SnapshotVersionAsync() => ScalarAsync<long>("select version from snapshot where id = 1");

    private async Task<int> CountAsync(string sql, object arg)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());
}
