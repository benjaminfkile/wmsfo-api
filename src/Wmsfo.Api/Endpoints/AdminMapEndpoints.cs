using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Endpoints.Impact;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 Maps, api.md 11.6 (Admin, the `maps` capability). A map is a
// tile package under maps/{packageKey}/ that the tile CLI uploads through the
// multipart uploads this API starts and signs; the API reads nothing of an
// archive but its 127 byte header. Upload ids are never stored: every call
// resolves an archive's open upload by listing the uploads under its key.
// Patch and delete run the [snapshot] frame when the current event uses the
// map and a plain transaction otherwise; every write records an audit row on
// entity tracker_map.
public static class AdminMapEndpoints
{
    public const string Tag = "AdminMaps";
    public const string AuditEntity = "tracker_map";
    public const string ArchiveContentType = "application/octet-stream";
    public const string ManifestContentType = "application/json; charset=utf-8";
    public const string Tiles = "tiles";
    public const string Terrain = "terrain";
    public const int MaxPartNumber = 10_000;
    public const int MaxPartsPerRequest = 100;

    private static readonly HashSet<string> CreateFields = new(StringComparer.Ordinal)
    {
        "name", "bbox", "minZoom", "maxZoom", "terrainMaxZoom", "sourceBuild",
    };

    public static string ArchiveKey(string prefix, string file) => prefix + "/" + file + ".pmtiles";

    public static string ManifestKey(string prefix) => prefix + "/manifest.json";

    // contracts 1.1: the lowercase hex sha256 of the canonical
    // { bbox, minZoom, maxZoom, terrainMaxZoom }, the rule the seeded
    // map's TrackerThemeSeed.ValleyPackageKey follows.
    public static string PackageKey(Bbox bbox, int minZoom, int maxZoom, int? terrainMaxZoom) =>
        TrackerThemeSeed.PackageKeyOf(new TrackerThemeSeed.PackageIdentity(
            new TrackerThemeSeed.PackageBbox(bbox.West, bbox.South, bbox.East, bbox.North),
            minZoom, maxZoom, terrainMaxZoom));

    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapCreate(app);
        MapParts(app);
        MapComplete(app);
        MapConfirm(app);
        MapPatch(app);
        MapDelete(app);
    }

    // GET /admin/maps → 200 { items: TrackerMap[] } by name, id.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/maps",
            async (WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<TrackerMapDto>();
                await using var cmd = new NpgsqlCommand(SelectSql + " order by m.name, m.id;", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) items.Add(ReadRow(reader, options));
                return Results.Ok(new ItemsResponse<TrackerMapDto> { Items = items });
            })
            .WithTags(Tag)
            .Produces<ItemsResponse<TrackerMapDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Maps)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/maps → 201 TrackerMapUpload, or 200 for a reused pending
    // row. Validate, key, insert or reuse under the key's row lock, then
    // resolve one open upload per declared archive.
    private static void MapCreate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/maps",
            async (HttpRequest request, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit,
                   WmsfoOptions options, IObjectStore store, SchemaValidator validator,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                var root = await ReadObjectAsync(request, ct);
                var v = new RequestValidation();
                foreach (var p in root.EnumerateObject())
                {
                    if (!CreateFields.Contains(p.Name)) v.Field(p.Name, "is not a map field");
                }
                var name = ReadName(root, v);
                var bbox = root.TryGetProperty("bbox", out var bboxEl)
                    ? EventTrackerRules.ReadBbox(bboxEl, "bbox", validator, v)
                    : Missing<Bbox>("bbox", v);
                var minZoom = ReadInt(root, "minZoom", 0, 15, required: true, v);
                var maxZoom = ReadInt(root, "maxZoom", 8, 15, required: true, v);
                if (minZoom is int lo && maxZoom is int hi && hi < lo) v.Field("maxZoom", "must be at least minZoom");
                var terrainMaxZoom = ReadInt(root, "terrainMaxZoom", 8, 13, required: false, v);
                var sourceBuild = ReadSourceBuild(root, v);
                v.ThrowIfInvalid();
                var email = AdminHelpers.RequireAdminEmail(ctx);

                var key = PackageKey(bbox!, minZoom!.Value, maxZoom!.Value, terrainMaxZoom);
                var prefix = "maps/" + key;
                var (id, created) = await snap.RunWithoutSnapshotAsync<(long, bool)>(async (conn, tx, token) =>
                {
                    var existing = await LockByKeyAsync(conn, tx, key, token);
                    if (existing is (long existingId, string state))
                    {
                        if (state == "ready") throw PackageExists();
                        return (existingId, false);
                    }
                    long newId;
                    await using (var insert = new NpgsqlCommand(@"
insert into tracker_map (name, package_key, prefix, bbox, min_zoom, max_zoom, terrain_max_zoom, source_build,
                         state, created_by, updated_by)
values ($1, $2, $3, $4, $5, $6, $7, $8, 'pending', $9, $9)
on conflict (package_key) do nothing
returning id;", conn, tx))
                    {
                        insert.Parameters.Add(Text(name!));
                        insert.Parameters.Add(Text(key));
                        insert.Parameters.Add(Text(prefix));
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = EventTrackerRules.Serialize(bbox!) });
                        insert.Parameters.Add(SmallInt(minZoom.Value));
                        insert.Parameters.Add(SmallInt(maxZoom.Value));
                        insert.Parameters.Add(SmallInt(terrainMaxZoom));
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = (object?)sourceBuild ?? DBNull.Value });
                        insert.Parameters.Add(Text(email));
                        if (await insert.ExecuteScalarAsync(token) is long inserted) newId = inserted;
                        else
                        {
                            // A concurrent create inserted the key first; its row decides.
                            var raced = await LockByKeyAsync(conn, tx, key, token) ?? throw NotFound();
                            if (raced.State == "ready") throw PackageExists();
                            return (raced.Id, false);
                        }
                    }
                    var dto = await ReadByIdAsync(conn, tx, newId, options, token) ?? throw NotFound();
                    await audit.RecordAsync(conn, tx, "create", AuditEntity, IdText(newId), before: null, after: dto, token);
                    return (newId, true);
                }, ct);

                var logger = loggers.CreateLogger("Wmsfo.Api.Maps");
                var open = await ObjectCallAsync(() => store.ListMultipartUploadsAsync(prefix + "/", ct), logger, ct);
                var upload = new TrackerMapUploadDto
                {
                    Id = id,
                    PackageKey = key,
                    Uploads = new TrackerMapUploadsDto
                    {
                        Tiles = new TrackerMapUploadIdDto
                        {
                            UploadId = await ResolveUploadAsync(store, ArchiveKey(prefix, Tiles), open, start: true, logger, ct) ?? "",
                        },
                    },
                };
                if (terrainMaxZoom is not null)
                {
                    upload.Uploads.Terrain = new TrackerMapUploadIdDto
                    {
                        UploadId = await ResolveUploadAsync(store, ArchiveKey(prefix, Terrain), open, start: true, logger, ct) ?? "",
                    };
                }
                return Results.Json(upload, statusCode: created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
            })
            .WithTags(Tag)
            .Accepts<CreateTrackerMapRequest>("application/json")
            .Produces<TrackerMapUploadDto>(StatusCodes.Status201Created)
            .Produces<TrackerMapUploadDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Maps)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/maps/{id}/parts → 200 TrackerMapParts: one presigned
    // UploadPart URL per number on the archive's open upload (started when
    // none is open), 15 minutes each.
    private static void MapParts(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/maps/{id:long}/parts",
            async (long id, HttpRequest request, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit,
                   WmsfoConnectionStrings connections, WmsfoOptions options, IObjectStore store,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                var root = await ReadObjectAsync(request, ct);
                var row = await ReadPendingAsync(connections, options, id, ct);
                var v = new RequestValidation();
                foreach (var p in root.EnumerateObject())
                {
                    if (p.Name is not ("file" or "partNumbers")) v.Field(p.Name, "is not a parts field");
                }
                var file = ReadFile(root, row, v);
                var numbers = ReadPartNumbers(root, v);
                v.ThrowIfInvalid();

                var logger = loggers.CreateLogger("Wmsfo.Api.Maps");
                var key = ArchiveKey(row.Prefix, file!);
                var open = await ObjectCallAsync(() => store.ListMultipartUploadsAsync(key, ct), logger, ct);
                var uploadId = (await ResolveUploadAsync(store, key, open, start: true, logger, ct))!;
                var expiresAt = DateTimeOffset.UtcNow.Add(ObjectTags.PresignLifetime);
                var parts = new TrackerMapPartsDto { File = file!, ExpiresAt = expiresAt };
                foreach (var n in numbers!)
                {
                    parts.Parts.Add(new TrackerMapPartUrlDto
                    {
                        PartNumber = n,
                        Url = store.PresignUploadPart(key, uploadId, n, ObjectTags.PresignLifetime),
                    });
                }

                await snap.RunWithoutSnapshotAsync<object?>(async (conn, tx, token) =>
                {
                    var before = await LockPendingAsync(conn, tx, id, options, token);
                    await audit.RecordAsync(conn, tx, "parts", AuditEntity, IdText(id),
                        before, new { file, partNumbers = numbers }, token);
                    return null;
                }, ct);
                return Results.Ok(parts);
            })
            .WithTags(Tag)
            .Accepts<TrackerMapPartsRequest>("application/json")
            .Produces<TrackerMapPartsDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Maps)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/maps/{id}/complete → 200 TrackerMap (still pending): joins
    // the listed parts of the archive's open upload; a store refusal is 400
    // at parts.
    private static void MapComplete(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/maps/{id:long}/complete",
            async (long id, HttpRequest request, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit,
                   WmsfoConnectionStrings connections, WmsfoOptions options, IObjectStore store,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                var root = await ReadObjectAsync(request, ct);
                var row = await ReadPendingAsync(connections, options, id, ct);
                var v = new RequestValidation();
                foreach (var p in root.EnumerateObject())
                {
                    if (p.Name is not ("file" or "parts")) v.Field(p.Name, "is not a complete field");
                }
                var file = ReadFile(root, row, v);
                var parts = ReadCompleteParts(root, v);
                v.ThrowIfInvalid();

                var logger = loggers.CreateLogger("Wmsfo.Api.Maps");
                var key = ArchiveKey(row.Prefix, file!);
                var open = await ObjectCallAsync(() => store.ListMultipartUploadsAsync(key, ct), logger, ct);
                var uploadId = await ResolveUploadAsync(store, key, open, start: false, logger, ct);
                if (uploadId is null) RequestValidation.Throw("parts", "the archive has no open upload");
                try
                {
                    await store.CompleteMultipartAsync(key, uploadId!, parts!, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "map upload completion refused mapId={MapId} file={File}", id, file);
                    RequestValidation.Throw("parts", "the store refused the completion: a part is missing or an etag is wrong");
                }

                var dto = await snap.RunWithoutSnapshotAsync<TrackerMapDto>(async (conn, tx, token) =>
                {
                    var before = await LockPendingAsync(conn, tx, id, options, token);
                    before.Audit = await audit.RecordAsync(conn, tx, "complete", AuditEntity, IdText(id),
                        before, new { file, partCount = parts!.Count }, token);
                    return before;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags(Tag)
            .Accepts<TrackerMapCompleteRequest>("application/json")
            .Produces<TrackerMapDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Maps)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/maps/{id}/confirm → 200 TrackerMap (ready). Head each
    // declared archive, check its PMTiles header against the row, write
    // manifest.json, then mark the row ready. A ready row answers as it is.
    private static void MapConfirm(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/maps/{id:long}/confirm",
            async (long id, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit,
                   WmsfoConnectionStrings connections, WmsfoOptions options, IObjectStore store,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                TrackerMapDto row;
                await using (var conn = new NpgsqlConnection(connections.App))
                {
                    await conn.OpenAsync(ct);
                    row = await ReadByIdAsync(conn, null, id, options, ct) ?? throw NotFound();
                }
                if (row.State == "ready") return Results.Ok(row);

                var logger = loggers.CreateLogger("Wmsfo.Api.Maps");
                var archives = new List<(string File, int? MinZoom, int MaxZoom)> { (Tiles, row.MinZoom, row.MaxZoom) };
                if (row.TerrainMaxZoom is int terrainMax) archives.Add((Terrain, null, terrainMax));
                var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var (file, minZoom, maxZoom) in archives)
                {
                    var key = ArchiveKey(row.Prefix, file);
                    var head = await ObjectCallAsync(() => store.HeadObjectAsync(key, ct), logger, ct)
                        ?? throw UploadNotFound(file);
                    var header = await ObjectCallAsync(() => store.GetObjectRangeAsync(key, 0, PmtilesHeader.Length - 1, ct), logger, ct)
                        ?? throw UploadNotFound(file);
                    var reason = PmtilesHeader.Check(header, row.Bbox, minZoom, maxZoom);
                    if (reason is not null) throw PackageInvalid(file, reason);
                    lengths[file] = head.ContentLength;
                }

                var manifest = Clone(row);
                manifest.State = "ready";
                manifest.BuiltAt = DateTimeOffset.UtcNow;
                manifest.TilesBytes = lengths[Tiles];
                manifest.TerrainBytes = lengths.TryGetValue(Terrain, out var t) ? t : null;
                manifest.UpdatedBy = email;
                manifest.UpdatedAt = manifest.BuiltAt.Value;
                manifest.Audit = null;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(ThemeStyles.ObjectCallTimeout);
                    await store.PutObjectAsync(ManifestKey(row.Prefix), CanonicalJson.SerializeToUtf8Bytes(manifest),
                        ManifestContentType, ThemeStyles.ImmutableCacheControl, tag: null, cts.Token);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "map manifest write failed mapId={MapId}", id);
                    throw new ApiException(StatusCodes.Status502BadGateway, ApiErrorCodes.UpstreamFailed,
                        "the manifest write failed");
                }

                var dto = await snap.RunWithoutSnapshotAsync<TrackerMapDto>(async (conn, tx, token) =>
                {
                    var before = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    int updated;
                    await using (var update = new NpgsqlCommand(@"
update tracker_map
set state = 'ready', built_at = now(), tiles_bytes = $1, terrain_bytes = $2, updated_by = $3, updated_at = now()
where id = $4 and state = 'pending';", conn, tx))
                    {
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = manifest.TilesBytes!.Value });
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)manifest.TerrainBytes ?? DBNull.Value });
                        update.Parameters.Add(Text(email));
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        updated = await update.ExecuteNonQueryAsync(token);
                    }
                    var after = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    if (updated == 0) return after;
                    after.Audit = await audit.RecordAsync(conn, tx, "confirm", AuditEntity, IdText(id), before, after, token);
                    return after;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags(Tag)
            .Produces<TrackerMapDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Maps)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PATCH /admin/maps/{id} → 200 TrackerMap. The name only.
    private static void MapPatch(IEndpointRouteBuilder app)
    {
        app.MapPatch("/admin/maps/{id:long}",
            async (long id, HttpRequest request, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit,
                   WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                var root = await ReadObjectAsync(request, ct);
                var v = new RequestValidation();
                foreach (var p in root.EnumerateObject())
                {
                    if (p.Name != "name") v.Field(p.Name, "only name can change");
                }
                var name = ReadName(root, v);
                v.ThrowIfInvalid();
                var email = AdminHelpers.RequireAdminEmail(ctx);
                bool snapshot;
                await using (var conn = new NpgsqlConnection(connections.App))
                {
                    await conn.OpenAsync(ct);
                    _ = await ReadByIdAsync(conn, null, id, options, ct) ?? throw NotFound();
                    snapshot = await CurrentEventUsesAsync(conn, [id], ct);
                }

                var dto = await RunFrameAsync<TrackerMapDto>(snap, snapshot, async (conn, tx, token) =>
                {
                    await LockAsync(conn, tx, id, token);
                    var before = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    await using (var update = new NpgsqlCommand(
                        "update tracker_map set name = $1, updated_by = $2, updated_at = now() where id = $3;", conn, tx))
                    {
                        update.Parameters.Add(Text(name!));
                        update.Parameters.Add(Text(email));
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await update.ExecuteNonQueryAsync(token);
                    }
                    var updated = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    updated.Audit = await audit.RecordAsync(conn, tx, "update", AuditEntity, IdText(id), before, updated, token);
                    return updated;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags(Tag)
            .Accepts<PatchTrackerMapRequest>("application/json")
            .Produces<TrackerMapDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Maps)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // DELETE /admin/maps/{id} → 204. Optional { replacementId }. Preview,
    // apply, record before = { ...dto, impact }; after commit abort the open
    // uploads and delete the objects under the prefix, except under basemap.
    private static void MapDelete(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/maps/{id:long}",
            async (long id, HttpRequest request, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit,
                   WmsfoConnectionStrings connections, WmsfoOptions options, IObjectStore store,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                var replacementId = await ReadDeleteBodyAsync(request, ct);
                bool snapshot;
                await using (var conn = new NpgsqlConnection(connections.App))
                {
                    await conn.OpenAsync(ct);
                    _ = await ReadByIdAsync(conn, null, id, options, ct) ?? throw NotFound();
                    if (replacementId is long r)
                        await TrackerMapImpactQueries.RequireReplacementAsync(conn, null, id, r, ct);
                    snapshot = await CurrentEventUsesAsync(conn, replacementId is long rr ? [id, rr] : [id], ct);
                }

                var prefix = await RunFrameAsync<string>(snap, snapshot, async (conn, tx, token) =>
                {
                    await LockAsync(conn, tx, id, token);
                    var before = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    var impact = await TrackerMapImpactQueries.PreviewAsync(conn, tx, id, token);
                    await TrackerMapImpactQueries.ApplyAsync(conn, tx, id, replacementId, token);
                    await audit.RecordAsync(conn, tx, "delete", AuditEntity, IdText(id),
                        before: ImpactBefore.Combine(before, impact), after: null, token);
                    return before.Prefix;
                }, ct);

                if (prefix != TrackerThemeSeed.ValleyPrefix)
                    await RemovePackageObjectsAsync(store, prefix, loggers.CreateLogger("Wmsfo.Api.Maps"), ct);
                return Results.NoContent();
            })
            .WithTags(Tag)
            .Accepts<DeleteTrackerMapRequest>(isOptional: true, "application/json")
            .Produces(StatusCodes.Status204NoContent)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Maps)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // Aborts every open upload and deletes every object under the prefix;
    // each failure is logged and the rest go on.
    private static async Task RemovePackageObjectsAsync(IObjectStore store, string prefix, ILogger logger, CancellationToken ct)
    {
        var under = prefix + "/";
        try
        {
            foreach (var upload in await store.ListMultipartUploadsAsync(under, ct))
            {
                try { await store.AbortMultipartAsync(upload.Key, upload.UploadId, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "map upload abort failed key={Key}", upload.Key);
                }
            }
            var keys = new List<string>();
            await foreach (var entry in store.ListPrefixAsync(under, ct)) keys.Add(entry.Key);
            foreach (var key in keys)
            {
                try { await store.DeleteObjectAsync(key, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "map object delete failed key={Key}", key);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "map object cleanup failed prefix={Prefix}", prefix);
        }
    }

    // --- request bodies ---

    private static async Task<JsonElement> ReadObjectAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                RequestValidation.Throw("body", "must be a JSON object");
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            RequestValidation.Throw("body", "is not valid JSON");
            throw; // unreachable
        }
    }

    // The optional delete body: empty, or { "replacementId": <id> | null }.
    private static async Task<long?> ReadDeleteBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var body = JsonSerializer.Deserialize<DeleteTrackerMapRequest>(text, CanonicalJson.Options);
            return body?.ReplacementId;
        }
        catch (JsonException)
        {
            RequestValidation.Throw("replacementId", "the body must be { replacementId } with a map id or null");
            return null; // unreachable
        }
    }

    private static T? Missing<T>(string field, RequestValidation v) where T : class
    {
        v.Field(field, "is required");
        return null;
    }

    private static string? ReadName(JsonElement root, RequestValidation v)
    {
        if (!root.TryGetProperty("name", out var name))
        {
            v.Field("name", "is required");
            return null;
        }
        var text = name.ValueKind == JsonValueKind.String ? name.GetString()!.Trim() : null;
        if (text is null || text.Length is < 1 or > 200)
        {
            v.Field("name", "must be 1 to 200 characters");
            return null;
        }
        return text;
    }

    // An integer from min to max; absent or null is allowed when not required.
    private static int? ReadInt(JsonElement root, string field, int min, int max, bool required, RequestValidation v)
    {
        if (!root.TryGetProperty(field, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            if (required) v.Field(field, "is required");
            return null;
        }
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var n) || n < min || n > max)
        {
            v.Field(field, string.Create(CultureInfo.InvariantCulture, $"must be an integer from {min} to {max}"));
            return null;
        }
        return n;
    }

    private static DateOnly? ReadSourceBuild(JsonElement root, RequestValidation v)
    {
        if (!root.TryGetProperty("sourceBuild", out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(el.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        v.Field("sourceBuild", "must be a YYYY-MM-DD date or null");
        return null;
    }

    // `file` names an archive the row declares: tiles, or terrain when the
    // row has a terrain zoom.
    private static string? ReadFile(JsonElement root, TrackerMapDto row, RequestValidation v)
    {
        var file = root.TryGetProperty("file", out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        if (file == Tiles || (file == Terrain && row.TerrainMaxZoom is not null)) return file;
        v.Field("file", row.TerrainMaxZoom is null ? "must be tiles" : "must be tiles or terrain");
        return null;
    }

    private static List<int>? ReadPartNumbers(JsonElement root, RequestValidation v)
    {
        const string rule = "must be 1 to 100 distinct integers from 1 to 10000";
        if (!root.TryGetProperty("partNumbers", out var el) || el.ValueKind != JsonValueKind.Array)
        {
            v.Field("partNumbers", rule);
            return null;
        }
        var numbers = new List<int>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var n) || n is < 1 or > MaxPartNumber)
            {
                v.Field("partNumbers", rule);
                return null;
            }
            numbers.Add(n);
        }
        if (numbers.Count is < 1 or > MaxPartsPerRequest || numbers.Distinct().Count() != numbers.Count)
        {
            v.Field("partNumbers", rule);
            return null;
        }
        return numbers;
    }

    private static List<MultipartPart>? ReadCompleteParts(JsonElement root, RequestValidation v)
    {
        const string rule = "must list 1 to 10000 parts in ascending partNumber order, each with its etag";
        if (!root.TryGetProperty("parts", out var el) || el.ValueKind != JsonValueKind.Array)
        {
            v.Field("parts", rule);
            return null;
        }
        var parts = new List<MultipartPart>();
        var previous = 0;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("partNumber", out var numberEl) || numberEl.ValueKind != JsonValueKind.Number
                || !numberEl.TryGetInt32(out var n) || n <= previous || n > MaxPartNumber
                || !item.TryGetProperty("etag", out var etagEl) || etagEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(etagEl.GetString())
                || item.EnumerateObject().Any(p => p.Name is not ("partNumber" or "etag")))
            {
                v.Field("parts", rule);
                return null;
            }
            previous = n;
            parts.Add(new MultipartPart(n, etagEl.GetString()!));
        }
        if (parts.Count == 0)
        {
            v.Field("parts", rule);
            return null;
        }
        return parts;
    }

    // --- object store ---

    // The open upload of `key` among `open`, or a new one when `start`; null
    // when none is open and `start` is false.
    private static async Task<string?> ResolveUploadAsync(
        IObjectStore store, string key, IReadOnlyList<MultipartUploadEntry> open, bool start, ILogger logger, CancellationToken ct)
    {
        var found = open.FirstOrDefault(u => u.Key == key);
        if (found is not null) return found.UploadId;
        if (!start) return null;
        return await ObjectCallAsync(
            () => store.StartMultipartAsync(key, ArchiveContentType, ThemeStyles.ImmutableCacheControl, ct), logger, ct);
    }

    // One object call with the 3 s limit; a failure is 502 upstream_failed.
    private static async Task<T> ObjectCallAsync<T>(Func<Task<T>> call, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await call().WaitAsync(ThemeStyles.ObjectCallTimeout, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "map object call failed");
            throw new ApiException(StatusCodes.Status502BadGateway, ApiErrorCodes.UpstreamFailed,
                "an object store call failed");
        }
    }

    // --- rows ---

    // The row for parts and complete: 404 when missing, 409 package_exists
    // when ready.
    private static async Task<TrackerMapDto> ReadPendingAsync(
        WmsfoConnectionStrings connections, WmsfoOptions options, long id, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connections.App);
        await conn.OpenAsync(ct);
        var row = await ReadByIdAsync(conn, null, id, options, ct) ?? throw NotFound();
        if (row.State == "ready") throw PackageExists();
        return row;
    }

    private static async Task<TrackerMapDto> LockPendingAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, WmsfoOptions options, CancellationToken ct)
    {
        await LockAsync(conn, tx, id, ct);
        var row = await ReadByIdAsync(conn, tx, id, options, ct) ?? throw NotFound();
        if (row.State == "ready") throw PackageExists();
        return row;
    }

    private static async Task<(long Id, string State)?> LockByKeyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select id, state from tracker_map where package_key = $1 for update;", conn, tx);
        cmd.Parameters.Add(Text(key));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetInt64(0), reader.GetString(1));
    }

    private static async Task LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select 1 from tracker_map where id = $1 for update;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        if (await cmd.ExecuteScalarAsync(ct) is null) throw NotFound();
    }

    // The [snapshot] rule of a map write: the current event's map is one of
    // the rows.
    private static async Task<bool> CurrentEventUsesAsync(NpgsqlConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select exists (select 1 from event where is_current and tracker_map_id = any($1));", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = ids.ToArray() });
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<T> RunFrameAsync<T>(
        AdminSnapshotTransaction snap, bool snapshot, AdminSnapshotTransaction.WriteFunc<T> write, CancellationToken ct)
    {
        if (!snapshot) return await snap.RunWithoutSnapshotAsync(write, ct);
        var (value, _) = await snap.RunAsync(write, ct);
        return value;
    }

    private const string SelectSql = @"
select m.id, m.name, m.package_key, m.prefix, m.bbox::text, m.min_zoom, m.max_zoom, m.terrain_max_zoom,
       m.tiles_bytes, m.terrain_bytes, m.source_build, m.state, m.built_at,
       (select count(*)::int from event e where e.tracker_map_id = m.id),
       m.created_by, m.created_at, m.updated_by, m.updated_at,
       a.action, a.actor, a.at
from tracker_map m
left join lateral (
  select action, actor, at from audit_log
  where entity = 'tracker_map' and entity_id = m.id::text
  order by id desc limit 1
) a on true";

    private static async Task<TrackerMapDto?> ReadByIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, WmsfoOptions options, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(SelectSql + " where m.id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRow(reader, options) : null;
    }

    // tilesUrl and terrainUrl as the snapshot builds them.
    private static TrackerMapDto ReadRow(NpgsqlDataReader reader, WmsfoOptions options)
    {
        var cdn = options.CdnBaseUrl.TrimEnd('/');
        var prefix = reader.GetString(3);
        int? terrainMaxZoom = reader.IsDBNull(7) ? null : reader.GetInt16(7);
        var dto = new TrackerMapDto
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            PackageKey = reader.GetString(2),
            Prefix = prefix,
            Bbox = EventTrackerRules.ParseStored(reader.GetString(4)),
            MinZoom = reader.GetInt16(5),
            MaxZoom = reader.GetInt16(6),
            TerrainMaxZoom = terrainMaxZoom,
            TilesBytes = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            TerrainBytes = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            SourceBuild = reader.IsDBNull(10)
                ? null
                : reader.GetFieldValue<DateOnly>(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            State = reader.GetString(11),
            BuiltAt = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            TilesUrl = cdn + "/" + prefix + "/tiles.pmtiles",
            TerrainUrl = terrainMaxZoom is null ? null : cdn + "/" + prefix + "/terrain.pmtiles",
            EventCount = reader.GetInt32(13),
            CreatedBy = reader.GetString(14),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(15),
            UpdatedBy = reader.GetString(16),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(17),
        };
        if (!reader.IsDBNull(18))
        {
            dto.Audit = new AuditStampDto
            {
                Action = reader.GetString(18),
                By = reader.GetString(19),
                At = reader.GetFieldValue<DateTimeOffset>(20),
            };
        }
        return dto;
    }

    private static TrackerMapDto Clone(TrackerMapDto dto) =>
        JsonSerializer.Deserialize<TrackerMapDto>(JsonSerializer.Serialize(dto, CanonicalJson.Options), CanonicalJson.Options)!;

    private static NpgsqlParameter Text(string value) => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter SmallInt(int? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Smallint, Value = value is int n ? (object)(short)n : DBNull.Value };

    private static string IdText(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "map not found");

    private static ApiException PackageExists() =>
        new(StatusCodes.Status409Conflict, "package_exists", "a ready map has this package");

    private static ApiException UploadNotFound(string file) =>
        new(StatusCodes.Status404NotFound, "upload_not_found", $"{file} was not uploaded", new UploadFileDetails(file));

    private static ApiException PackageInvalid(string field, string reason) =>
        new(StatusCodes.Status409Conflict, "package_invalid", $"the {field} archive fails the {reason} check",
            new PackageInvalidDetails(field, reason));
}

// details of 409 package_invalid on a map confirm: the archive (tiles or
// terrain) and the failed check (magic, version, bounds, minZoom, maxZoom).
public sealed record PackageInvalidDetails(string Field, string Reason);
