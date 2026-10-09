using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Chores;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Data;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.IntegrationTests;

// The map endpoints of contracts 4.5 Maps and api.md 11.6: create with the
// package key and pending reuse, signed parts, complete, the 127 byte header
// confirm with manifest.json, patch, impact, delete with an optional
// replacement, the pending map sweep, the audit rows, and the capability.
public sealed class A96MapEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Cdn = "https://cdn.example";
    private const string Immutable = "public, max-age=31536000, immutable";

    // The package box the tests declare and a larger one the archives carry.
    private static readonly Bbox Box = new() { West = -114.5, South = 46.5, East = -113.5, North = 47.0 };
    private static readonly Bbox ArchiveBounds = new() { West = -115.0, South = 46.0, East = -113.0, North = 47.5 };

    private readonly PostgresFixture _fixture;
    private A96Host? _host;

    public A96MapEndpointsTests(PostgresFixture fixture)
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
            "delete from tracker_map where prefix <> 'basemap';",
            "delete from audit_log where entity = 'tracker_map';",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
        _host = await A96Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    // ---------- create ----------

    [Fact]
    public async Task Create_answers_201_with_a_pending_row_and_one_upload_per_declared_archive()
    {
        var response = await SendAsync(HttpMethod.Post, "/admin/maps", Body("With terrain", Box, 0, 14, 12).ToJsonString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var root = doc.RootElement;
        var key = AdminMapEndpoints.PackageKey(Box, 0, 14, 12);
        Assert.Equal(key, root.GetProperty("packageKey").GetString());
        var tilesUpload = root.GetProperty("uploads").GetProperty("tiles").GetProperty("uploadId").GetString();
        var terrainUpload = root.GetProperty("uploads").GetProperty("terrain").GetProperty("uploadId").GetString();
        Assert.False(string.IsNullOrEmpty(tilesUpload));
        Assert.False(string.IsNullOrEmpty(terrainUpload));
        Assert.Equal($"maps/{key}/tiles.pmtiles", _host!.Store.Uploads[tilesUpload!].Key);
        Assert.Equal($"maps/{key}/terrain.pmtiles", _host.Store.Uploads[terrainUpload!].Key);
        Assert.Equal("application/octet-stream", _host.Store.Uploads[tilesUpload!].ContentType);
        Assert.Equal(Immutable, _host.Store.Uploads[tilesUpload!].CacheControl);

        var listed = await FindInListAsync(Id(doc));
        Assert.Equal("pending", listed.GetProperty("state").GetString());
        Assert.Equal("maps/" + key, listed.GetProperty("prefix").GetString());
        Assert.Equal($"{Cdn}/maps/{key}/tiles.pmtiles", listed.GetProperty("tilesUrl").GetString());
        Assert.Equal($"{Cdn}/maps/{key}/terrain.pmtiles", listed.GetProperty("terrainUrl").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("tilesBytes").ValueKind);
        Assert.Equal(0, listed.GetProperty("eventCount").GetInt32());
        Assert.Equal("create", listed.GetProperty("audit").GetProperty("action").GetString());

        var bare = await SendAsync(HttpMethod.Post, "/admin/maps", Body("No terrain", Box, 0, 14, null).ToJsonString());
        Assert.Equal(HttpStatusCode.Created, bare.StatusCode);
        using var bareDoc = await ReadJsonAsync(bare);
        Assert.Equal(JsonValueKind.Null, bareDoc.RootElement.GetProperty("uploads").GetProperty("terrain").ValueKind);
        var bareKey = bareDoc.RootElement.GetProperty("packageKey").GetString();
        Assert.Single(_host.Store.Uploads.Values, u => u.Key.StartsWith($"maps/{bareKey}/", StringComparison.Ordinal));
        var bareListed = await FindInListAsync(Id(bareDoc));
        Assert.Equal(JsonValueKind.Null, bareListed.GetProperty("terrainUrl").ValueKind);
    }

    [Fact]
    public async Task Create_again_reuses_the_pending_row_and_its_open_uploads_and_restarts_a_lost_one()
    {
        var body = Body("Reused", Box, 2, 13, 11).ToJsonString();
        using var first = await ReadJsonAsync(await SendAsync(HttpMethod.Post, "/admin/maps", body));
        var second = await SendAsync(HttpMethod.Post, "/admin/maps", body);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var again = await ReadJsonAsync(second);
        Assert.Equal(first.RootElement.ToString(), again.RootElement.ToString());

        var terrain = first.RootElement.GetProperty("uploads").GetProperty("terrain").GetProperty("uploadId").GetString()!;
        _host!.Store.Uploads.TryRemove(terrain, out _);
        using var third = await ReadJsonAsync(await SendAsync(HttpMethod.Post, "/admin/maps", body));
        Assert.Equal(Id(first), Id(third));
        Assert.Equal(first.RootElement.GetProperty("uploads").GetProperty("tiles").GetProperty("uploadId").GetString(),
            third.RootElement.GetProperty("uploads").GetProperty("tiles").GetProperty("uploadId").GetString());
        Assert.NotEqual(terrain, third.RootElement.GetProperty("uploads").GetProperty("terrain").GetProperty("uploadId").GetString());
        Assert.Equal(1, await CountAsync("select count(*)::int from audit_log where entity = 'tracker_map' and entity_id = $1", Id(first).ToString()));
    }

    [Fact]
    public async Task A_ready_map_with_the_key_is_409_package_exists()
    {
        var body = Body("Taken", Box, 0, 12, null).ToJsonString();
        using var created = await ReadJsonAsync(await SendAsync(HttpMethod.Post, "/admin/maps", body));
        await ExecAsync($"update tracker_map set state = 'ready' where id = {Id(created)}");
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "/admin/maps", body), HttpStatusCode.Conflict, "package_exists");
    }

    [Fact]
    public async Task Every_field_rule_is_400_at_its_path()
    {
        var cases = new (Action<JsonObject> Mutate, string Path)[]
        {
            (b => b["name"] = "", "name"),
            (b => b["name"] = new string('n', 201), "name"),
            (b => b.Remove("name"), "name"),
            (b => b.Remove("bbox"), "bbox"),
            (b => b["bbox"] = BboxNode(new Bbox { West = -113, South = 46, East = -114, North = 47 }), "bbox"),
            (b => b["bbox"] = BboxNode(new Bbox { West = -130, South = 40, East = -105, North = 47 }), "bbox"),
            (b => b["bbox"] = JsonNode.Parse("""{"west":-114,"south":46,"east":-113}"""), "bbox"),
            (b => b["minZoom"] = 16, "minZoom"),
            (b => b["minZoom"] = "0", "minZoom"),
            (b => b.Remove("minZoom"), "minZoom"),
            (b => b["maxZoom"] = 7, "maxZoom"),
            (b => b["maxZoom"] = 16, "maxZoom"),
            (b => { b["minZoom"] = 12; b["maxZoom"] = 10; }, "maxZoom"),
            (b => b["terrainMaxZoom"] = 14, "terrainMaxZoom"),
            (b => b["terrainMaxZoom"] = 7, "terrainMaxZoom"),
            (b => b["sourceBuild"] = "2026-13-01", "sourceBuild"),
            (b => b["sourceBuild"] = "October 1", "sourceBuild"),
            (b => b["prefix"] = "maps/x", "prefix"),
        };
        foreach (var (mutate, path) in cases)
        {
            var body = Body("Rules", Box, 0, 14, 12);
            mutate(body);
            await AssertValidationAsync(await SendAsync(HttpMethod.Post, "/admin/maps", body.ToJsonString()), path);
        }
        Assert.Equal(0, await CountAsync("select count(*)::int from tracker_map where name = $1", "Rules"));
    }

    // ---------- parts ----------

    [Fact]
    public async Task Parts_answers_one_url_per_number_and_refuses_an_undeclared_archive_and_bad_numbers()
    {
        using var created = await ReadJsonAsync(await SendAsync(HttpMethod.Post, "/admin/maps",
            Body("Parts", Box, 0, 14, null).ToJsonString()));
        var id = Id(created);

        await AssertValidationAsync(await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/parts",
            """{"file":"terrain","partNumbers":[1]}"""), "file");
        foreach (var numbers in new[] { "[]", "[0]", "[10001]", "[1,1]", "[1.5]", "\"1\"",
            "[" + string.Join(",", Enumerable.Range(1, 101)) + "]" })
        {
            await AssertValidationAsync(await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/parts",
                $$"""{"file":"tiles","partNumbers":{{numbers}}}"""), "partNumbers");
        }
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Post, "/admin/maps/999999/parts", """{"file":"tiles","partNumbers":[1]}""")).StatusCode);

        var before = DateTimeOffset.UtcNow;
        var response = await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/parts", """{"file":"tiles","partNumbers":[3,1,2]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("tiles", doc.RootElement.GetProperty("file").GetString());
        var parts = doc.RootElement.GetProperty("parts").EnumerateArray().ToArray();
        Assert.Equal([3, 1, 2], parts.Select(p => p.GetProperty("partNumber").GetInt32()).ToArray());
        var uploadId = created.RootElement.GetProperty("uploads").GetProperty("tiles").GetProperty("uploadId").GetString();
        Assert.All(parts, p => Assert.Equal(
            $"https://example/local-upload/parts/{uploadId}/{p.GetProperty("partNumber").GetInt32()}",
            p.GetProperty("url").GetString()));
        var expiresAt = doc.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange(expiresAt, before.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));

        var after = await ScalarAsync<string>(
            $"select after::text from audit_log where entity = 'tracker_map' and entity_id = '{id}' and action = 'parts'");
        using var afterDoc = JsonDocument.Parse(after);
        Assert.Equal("tiles", afterDoc.RootElement.GetProperty("file").GetString());
        Assert.Equal([3, 1, 2], afterDoc.RootElement.GetProperty("partNumbers").EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }

    // ---------- the full path ----------

    [Fact]
    public async Task The_full_path_uploads_completes_and_confirms_with_the_manifest_and_every_audit_action()
    {
        var id = await CreateAsync("Full path", Box, 0, 14, 12);
        var tiles = Archive(0, 14, ArchiveBounds, 5000);
        var terrain = Archive(0, 12, ArchiveBounds, 3000);
        var completed = await UploadAsync(id, "tiles", tiles);
        Assert.Equal("pending", completed.GetProperty("state").GetString());
        Assert.Equal("complete", completed.GetProperty("audit").GetProperty("action").GetString());
        await UploadAsync(id, "terrain", terrain);

        var prefix = $"maps/{AdminMapEndpoints.PackageKey(Box, 0, 14, 12)}";
        Assert.Equal(tiles, _host!.Store.Objects[prefix + "/tiles.pmtiles"].Bytes);
        Assert.Equal(Immutable, _host.Store.Objects[prefix + "/tiles.pmtiles"].CacheControl);

        var response = await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var confirmed = await ReadJsonAsync(response);
        var root = confirmed.RootElement;
        Assert.Equal("ready", root.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("builtAt").ValueKind);
        Assert.Equal(tiles.Length, root.GetProperty("tilesBytes").GetInt64());
        Assert.Equal(terrain.Length, root.GetProperty("terrainBytes").GetInt64());
        Assert.Equal("confirm", root.GetProperty("audit").GetProperty("action").GetString());

        var manifest = _host.Store.Objects[prefix + "/manifest.json"];
        Assert.Equal(Immutable, manifest.CacheControl);
        Assert.Equal("application/json; charset=utf-8", manifest.ContentType);
        var dto = JsonSerializer.Deserialize<TrackerMapDto>(manifest.Bytes, CanonicalJson.Options)!;
        Assert.Equal("ready", dto.State);
        Assert.Equal(id, dto.Id);
        Assert.Equal(tiles.Length, dto.TilesBytes);
        Assert.Equal(manifest.Bytes, CanonicalJson.SerializeToUtf8Bytes(dto));

        // A ready row: confirm answers 200 and writes nothing; parts and complete are 409.
        var puts = _host.Store.PutKeys.Count;
        var audits = await CountAsync("select count(*)::int from audit_log where entity_id = $1", id.ToString());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/confirm", null)).StatusCode);
        Assert.Equal(puts, _host.Store.PutKeys.Count);
        Assert.Equal(audits, await CountAsync("select count(*)::int from audit_log where entity_id = $1", id.ToString()));
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/parts",
            """{"file":"tiles","partNumbers":[1]}"""), HttpStatusCode.Conflict, "package_exists");
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/complete",
            """{"file":"tiles","parts":[{"partNumber":1,"etag":"x"}]}"""), HttpStatusCode.Conflict, "package_exists");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Patch, $"/admin/maps/{id}", """{"name":"Renamed"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/admin/maps/{id}", null)).StatusCode);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select action, actor from audit_log where entity = 'tracker_map' and entity_id = $1 order by id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = id.ToString() });
        await using var reader = await cmd.ExecuteReaderAsync();
        var actions = new List<string>();
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(0));
            Assert.Equal("person:" + DevStaticTokens.AdminEmail, reader.GetString(1));
        }
        Assert.Equal(["create", "parts", "complete", "parts", "complete", "confirm", "update", "delete"], actions);
    }

    [Fact]
    public async Task Complete_with_a_wrong_etag_is_400_at_parts()
    {
        var id = await CreateAsync("Wrong etag", Box, 0, 14, null);
        using var parts = await ReadJsonAsync(await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/parts",
            """{"file":"tiles","partNumbers":[1]}"""));
        _host!.Store.UploadPart(parts.RootElement.GetProperty("parts")[0].GetProperty("url").GetString()!,
            Archive(0, 14, ArchiveBounds, 500));
        await AssertValidationAsync(await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/complete",
            """{"file":"tiles","parts":[{"partNumber":1,"etag":"\"0000\""}]}"""), "parts");
        await AssertValidationAsync(await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/complete",
            """{"file":"tiles","parts":[{"partNumber":2,"etag":"x"},{"partNumber":1,"etag":"y"}]}"""), "parts");
    }

    [Fact]
    public async Task Confirm_with_bounds_not_containing_the_box_is_409_package_invalid_and_the_row_stays_pending()
    {
        var id = await CreateAsync("Small archive", Box, 0, 14, null);
        var narrow = new Bbox { West = -114.4, South = 46.5, East = -113.5, North = 47.0 };
        await UploadAsync(id, "tiles", Archive(0, 14, narrow, 2000));
        var response = await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/confirm", null);
        using var error = await AssertErrorAsync(response, HttpStatusCode.Conflict, "package_invalid");
        Assert.Equal("tiles", error.RootElement.GetProperty("details").GetProperty("field").GetString());
        Assert.Equal("bounds", error.RootElement.GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal("pending", await ScalarAsync<string>($"select state from tracker_map where id = {id}"));
        Assert.False(_host!.Store.Objects.ContainsKey($"maps/{AdminMapEndpoints.PackageKey(Box, 0, 14, null)}/manifest.json"));
    }

    [Fact]
    public async Task Confirm_with_a_declared_terrain_archive_never_uploaded_is_404_upload_not_found()
    {
        var id = await CreateAsync("No terrain upload", Box, 0, 14, 12);
        await UploadAsync(id, "tiles", Archive(0, 14, ArchiveBounds, 2000));
        using var error = await AssertErrorAsync(await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/confirm", null),
            HttpStatusCode.NotFound, "upload_not_found");
        Assert.Equal("terrain", error.RootElement.GetProperty("details").GetProperty("file").GetString());
        Assert.Equal("pending", await ScalarAsync<string>($"select state from tracker_map where id = {id}"));
    }

    // ---------- patch ----------

    [Fact]
    public async Task Patch_name_bumps_the_snapshot_only_for_the_current_events_map()
    {
        var current = await CreateReadyAsync("Current map", Box);
        var other = await CreateReadyAsync("Other map", new Bbox { West = -100, South = 40, East = -99, North = 41 });
        await InsertEventAsync(2026, "Flight 2026", current: true, Box, current);

        var version = await SnapshotVersionAsync();
        var patched = await SendAsync(HttpMethod.Patch, $"/admin/maps/{current}", """{"name":"Valley floor"}""");
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        using (var doc = await ReadJsonAsync(patched))
        {
            Assert.Equal("Valley floor", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal("update", doc.RootElement.GetProperty("audit").GetProperty("action").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("eventCount").GetInt32());
        }
        Assert.True(await SnapshotVersionAsync() > version);

        version = await SnapshotVersionAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Patch, $"/admin/maps/{other}", """{"name":"Elsewhere"}""")).StatusCode);
        Assert.Equal(version, await SnapshotVersionAsync());

        await AssertValidationAsync(await SendAsync(HttpMethod.Patch, $"/admin/maps/{other}", """{"name":"x","minZoom":3}"""), "minZoom");
        await AssertValidationAsync(await SendAsync(HttpMethod.Patch, $"/admin/maps/{other}", """{"name":""}"""), "name");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Patch, "/admin/maps/999999", """{"name":"x"}""")).StatusCode);
    }

    // ---------- impact and delete ----------

    [Fact]
    public async Task Impact_lists_the_referencing_events_and_the_three_warnings()
    {
        var map = await CreateReadyAsync("Impact map", Box);
        await InsertEventAsync(2025, "Flight 2025", current: false, Box, map);
        await InsertEventAsync(2026, "Flight 2026", current: true, Box, map, statusId: 3);

        using var doc = await ReadJsonAsync(await SendAsync(HttpMethod.Get, $"/admin/maps/{map}/impact", null));
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("blocked").ValueKind);
        Assert.Empty(root.GetProperty("deletes").EnumerateArray());
        var group = Assert.Single(root.GetProperty("unlinks").EnumerateArray());
        Assert.Equal("event", group.GetProperty("entity").GetString());
        Assert.Equal(2, group.GetProperty("count").GetInt32());
        Assert.Equal(["Flight 2025", "Flight 2026"], group.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray());
        Assert.Equal(
            [
                "2 events lose their map; their viewers get Google Maps at the next snapshot.",
                "Flight 2026 is the current event; its viewers move to Google Maps at the next snapshot.",
                "Flight 2026 is live.",
            ],
            root.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray());

        var unused = await CreateReadyAsync("Unused map", new Bbox { West = -100, South = 40, East = -99, North = 41 });
        using var none = await ReadJsonAsync(await SendAsync(HttpMethod.Get, $"/admin/maps/{unused}/impact", null));
        Assert.Empty(none.RootElement.GetProperty("unlinks").EnumerateArray());
        Assert.Empty(none.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/admin/maps/999999/impact", null)).StatusCode);
    }

    [Fact]
    public async Task Delete_without_a_replacement_nulls_the_events_map_and_removes_the_objects_and_open_uploads()
    {
        var id = await CreateAsync("Doomed", Box, 0, 14, 12);
        var prefix = $"maps/{AdminMapEndpoints.PackageKey(Box, 0, 14, 12)}";
        await UploadAsync(id, "tiles", Archive(0, 14, ArchiveBounds, 1000));
        Assert.Single(_host!.Store.Uploads.Values, u => u.Key.StartsWith(prefix + "/", StringComparison.Ordinal));
        await _host.Store.PutObjectAsync("maps/elsewhere/tiles.pmtiles", new byte[] { 1 }, "application/octet-stream", Immutable);
        await ExecAsync($"update tracker_map set state = 'ready' where id = {id}");
        var current = await InsertEventAsync(2026, "Flight 2026", current: true, Box, id);
        var other = await InsertEventAsync(2025, "Flight 2025", current: false, Box, id);

        var version = await SnapshotVersionAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/admin/maps/{id}", null)).StatusCode);
        Assert.True(await SnapshotVersionAsync() > version);
        Assert.Equal(0, await CountAsync("select count(*)::int from tracker_map where id = $1", id));
        Assert.Equal(0, await CountAsync("select count(*)::int from event where id in ($1) and tracker_map_id is not null", current));
        Assert.Equal(0, await CountAsync("select count(*)::int from event where id in ($1) and tracker_map_id is not null", other));
        Assert.DoesNotContain(_host.Store.Objects.Keys, k => k.StartsWith(prefix + "/", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Store.Uploads.Values, u => u.Key.StartsWith(prefix + "/", StringComparison.Ordinal));
        Assert.True(_host.Store.Objects.ContainsKey("maps/elsewhere/tiles.pmtiles"));

        var before = await ScalarAsync<string>(
            $"select before::text from audit_log where entity = 'tracker_map' and entity_id = '{id}' and action = 'delete'");
        using var beforeDoc = JsonDocument.Parse(before);
        Assert.Equal("Doomed", beforeDoc.RootElement.GetProperty("name").GetString());
        Assert.Equal(2, beforeDoc.RootElement.GetProperty("impact").GetProperty("unlinks")[0].GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/admin/maps/{id}", null)).StatusCode);
    }

    [Fact]
    public async Task Delete_with_a_replacement_repoints_the_events_and_refuses_a_miss_a_pending_or_an_unknown_one()
    {
        var map = await CreateReadyAsync("Old map", Box);
        var e1 = await InsertEventAsync(2025, "Flight 2025", current: false, Box, map);
        var wideBox = new Bbox { West = -114.6, South = 46.5, East = -113.5, North = 47.0 };
        var e2 = await InsertEventAsync(2026, "Flight 2026", current: false, wideBox, map);

        var tooSmall = await CreateReadyAsync("Too small", Box, maxZoom: 13);
        using (var error = await ReadJsonAsync(await SendAsync(HttpMethod.Delete, $"/admin/maps/{map}", $$"""{"replacementId":{{tooSmall}}}""")))
        {
            Assert.Equal("validation_failed", error.RootElement.GetProperty("code").GetString());
            Assert.True(error.RootElement.GetProperty("details").GetProperty("fields").TryGetProperty("replacementId", out _));
            Assert.Equal(e2, error.RootElement.GetProperty("details").GetProperty("eventId").GetInt64());
        }

        var pending = await CreateAsync("Pending replacement", new Bbox { West = -115, South = 46, East = -113, North = 47.5 }, 0, 14, null);
        await AssertValidationAsync(await SendAsync(HttpMethod.Delete, $"/admin/maps/{map}", $$"""{"replacementId":{{pending}}}"""), "replacementId");
        await AssertValidationAsync(await SendAsync(HttpMethod.Delete, $"/admin/maps/{map}", $$"""{"replacementId":{{map}}}"""), "replacementId");
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Delete, $"/admin/maps/{map}", """{"replacementId":999999}""")).StatusCode);
        Assert.Equal(1, await CountAsync("select count(*)::int from tracker_map where id = $1", map));

        var wide = await CreateReadyAsync("Wide map", new Bbox { West = -115, South = 46, East = -113, North = 47.5 }, maxZoom: 12);
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Delete, $"/admin/maps/{map}", $$"""{"replacementId":{{wide}}}""")).StatusCode);
        Assert.Equal(2, await CountAsync("select count(*)::int from event where tracker_map_id = $1", wide));
        Assert.Equal(0, await CountAsync("select count(*)::int from tracker_map where id = $1", map));
        Assert.Equal(wide, await ScalarAsync<long>($"select tracker_map_id from event where id = {e1}"));
        Assert.Equal(wide, await ScalarAsync<long>($"select tracker_map_id from event where id = {e2}"));
    }

    [Fact]
    public async Task Deleting_the_seeded_basemap_row_leaves_its_objects()
    {
        var basemap = await ScalarAsync<long>("select id from tracker_map where prefix = 'basemap'");
        await _host!.Store.PutObjectAsync("basemap/tiles.pmtiles", new byte[] { 1, 2, 3 }, "application/octet-stream", "public, max-age=86400");
        await _host.Store.PutObjectAsync("basemap/terrain.pmtiles", new byte[] { 4, 5 }, "application/octet-stream", "public, max-age=86400");
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/admin/maps/{basemap}", null)).StatusCode);
        Assert.Equal(0, await CountAsync("select count(*)::int from tracker_map where id = $1", basemap));
        Assert.True(_host.Store.Objects.ContainsKey("basemap/tiles.pmtiles"));
        Assert.True(_host.Store.Objects.ContainsKey("basemap/terrain.pmtiles"));
    }

    // ---------- the sweep ----------

    [Fact]
    public async Task The_sweep_removes_an_old_pending_row_with_its_objects_and_uploads_and_leaves_fresh_and_ready_rows()
    {
        var old = await CreateAsync("Old pending", Box, 0, 14, 12);
        var oldPrefix = $"maps/{AdminMapEndpoints.PackageKey(Box, 0, 14, 12)}";
        await UploadAsync(old, "tiles", Archive(0, 14, ArchiveBounds, 1000));
        var freshBox = new Bbox { West = -100, South = 40, East = -99, North = 41 };
        var fresh = await CreateAsync("Fresh pending", freshBox, 0, 14, null);
        var freshPrefix = $"maps/{AdminMapEndpoints.PackageKey(freshBox, 0, 14, null)}";
        var ready = await CreateReadyAsync("Old ready", new Bbox { West = -90, South = 30, East = -89, North = 31 });
        await ExecAsync($"update tracker_map set created_at = now() - interval '2 days' where id in ({old}, {ready})");

        var sweeper = new PendingMapSweeper(TestConnections.For(_fixture.ConnectionString), _host!.Store,
            NullLogger<PendingMapSweeper>.Instance);
        Assert.Equal(1, await sweeper.RunOnceAsync(CancellationToken.None));

        Assert.Equal(0, await CountAsync("select count(*)::int from tracker_map where id = $1", old));
        Assert.Equal(1, await CountAsync("select count(*)::int from tracker_map where id = $1", fresh));
        Assert.Equal(1, await CountAsync("select count(*)::int from tracker_map where id = $1", ready));
        Assert.DoesNotContain(_host.Store.Objects.Keys, k => k.StartsWith(oldPrefix + "/", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Store.Uploads.Values, u => u.Key.StartsWith(oldPrefix + "/", StringComparison.Ordinal));
        Assert.Contains(_host.Store.Uploads.Values, u => u.Key.StartsWith(freshPrefix + "/", StringComparison.Ordinal));

        Assert.Equal(0, await sweeper.RunOnceAsync(CancellationToken.None));
    }

    // ---------- capability ----------

    [Fact]
    public async Task An_api_key_with_maps_reaches_the_list_and_one_without_is_403()
    {
        var with = await MintKeyAsync("a96-maps", "maps");
        var without = await MintKeyAsync("a96-events", "events");
        using (var req = KeyRequest(HttpMethod.Get, "/admin/maps", with))
            Assert.Equal(HttpStatusCode.OK, (await _host!.Client.SendAsync(req)).StatusCode);
        using (var req = KeyRequest(HttpMethod.Get, "/admin/maps", without))
            Assert.Equal(HttpStatusCode.Forbidden, (await _host!.Client.SendAsync(req)).StatusCode);
        using (var req = KeyRequest(HttpMethod.Get, "/admin/maps/1/impact", without))
            Assert.Equal(HttpStatusCode.Forbidden, (await _host!.Client.SendAsync(req)).StatusCode);
    }

    // ---------- helpers ----------

    private static JsonNode BboxNode(Bbox b) => JsonNode.Parse(JsonSerializer.Serialize(b, CanonicalJson.Options))!;

    private static JsonObject Body(string name, Bbox bbox, int minZoom, int maxZoom, int? terrainMaxZoom) => new()
    {
        ["name"] = name,
        ["bbox"] = BboxNode(bbox),
        ["minZoom"] = minZoom,
        ["maxZoom"] = maxZoom,
        ["terrainMaxZoom"] = terrainMaxZoom,
        ["sourceBuild"] = "2026-10-01",
    };

    // A synthetic archive: the 127 byte PMTiles v3 header padded to `length`.
    private static byte[] Archive(int minZoom, int maxZoom, Bbox bounds, int length)
    {
        var bytes = new byte[length];
        PmtilesHeader.Build(3, minZoom, maxZoom, bounds).CopyTo(bytes, 0);
        for (var i = PmtilesHeader.Length; i < length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private async Task<long> CreateAsync(string name, Bbox bbox, int minZoom, int maxZoom, int? terrainMaxZoom)
    {
        var response = await SendAsync(HttpMethod.Post, "/admin/maps", Body(name, bbox, minZoom, maxZoom, terrainMaxZoom).ToJsonString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return Id(doc);
    }

    private async Task<long> CreateReadyAsync(string name, Bbox bbox, int maxZoom = 14)
    {
        var id = await CreateAsync(name, bbox, 0, maxZoom, null);
        await ExecAsync($"update tracker_map set state = 'ready', built_at = now() where id = {id}");
        return id;
    }

    // Signs two parts, PUTs the archive in two halves through the part URLs,
    // and completes; answers the complete's TrackerMap.
    private async Task<JsonElement> UploadAsync(long id, string file, byte[] archive)
    {
        var response = await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/parts", $$"""{"file":"{{file}}","partNumbers":[1,2]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var parts = await ReadJsonAsync(response);
        var half = archive.Length / 2;
        var chunks = new[] { archive[..half], archive[half..] };
        var completed = new JsonArray();
        foreach (var (part, i) in parts.RootElement.GetProperty("parts").EnumerateArray().Select((p, i) => (p, i)))
        {
            var etag = _host!.Store.UploadPart(part.GetProperty("url").GetString()!, chunks[i]);
            completed.Add(new JsonObject { ["partNumber"] = part.GetProperty("partNumber").GetInt32(), ["etag"] = etag });
        }
        var body = new JsonObject { ["file"] = file, ["parts"] = completed };
        var done = await SendAsync(HttpMethod.Post, $"/admin/maps/{id}/complete", body.ToJsonString());
        var text = await done.Content.ReadAsStringAsync();
        Assert.True(done.StatusCode == HttpStatusCode.OK, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
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
        using var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/admin/maps", null));
        return list.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetInt64() == id).Clone();
    }

    // Asserts 400 validation_failed with the field in details.fields.
    private static async Task AssertValidationAsync(HttpResponseMessage response, string field)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 at {field}, got {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("validation_failed", doc.RootElement.GetProperty("code").GetString());
        var fields = doc.RootElement.GetProperty("details").GetProperty("fields");
        Assert.True(fields.TryGetProperty(field, out _), $"expected a failure at {field}: {text}");
    }

    private static async Task<JsonDocument> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"expected {(int)status} {code}, got {(int)response.StatusCode}: {text}");
        var doc = JsonDocument.Parse(text);
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
        return doc;
    }

    private static long Id(JsonDocument doc) => doc.RootElement.GetProperty("id").GetInt64();

    private async Task<long> InsertEventAsync(int year, string name, bool current, Bbox box, long? mapId, short statusId = 1)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at, tracker_bbox, tracker_map_id)
values ($1, $2, $3, $4, 'a96-test', now(), $5, $6)
returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = statusId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = current });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(box, CanonicalJson.Options) });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)mapId ?? DBNull.Value });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<long> SnapshotVersionAsync() => ScalarAsync<long>("select version from snapshot where id = 1");

    private async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

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
