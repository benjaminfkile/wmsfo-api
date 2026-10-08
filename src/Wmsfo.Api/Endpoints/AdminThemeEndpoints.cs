using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Endpoints.Impact;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 Themes, api.md 11a.10 (Admin, the `themes` capability). A
// theme's style body is an immutable CDN object at themes/{sha256}.json the
// API writes once per distinct body; its sprite set lives under
// themes/{id}/sprites/{indexSha256}/. Every write but create runs the
// [snapshot] frame when the current event enables the theme (or the
// replacement, or the flag's previous holder) and a plain transaction
// otherwise; every write records an audit row on entity tracker_theme.
public static partial class AdminThemeEndpoints
{
    public const string Tag = "AdminThemes";
    public const string AuditEntity = "tracker_theme";
    public const string JsonContentType = "application/json; charset=utf-8";
    public const string PngContentType = "image/png";
    public const int SpriteJsonMaxBytes = 1024 * 1024;
    public const int SpritePngMaxBytes = 8 * 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // The four files of a sprite set with their content types.
    public static readonly IReadOnlyList<(string File, string ContentType)> SpriteFiles =
    [
        ("sprite.json", JsonContentType),
        ("sprite.png", PngContentType),
        ("sprite@2x.json", JsonContentType),
        ("sprite@2x.png", PngContentType),
    ];

    private static readonly HashSet<string> CreateFields = new(StringComparer.Ordinal)
    {
        "renderer", "key", "name", "sortOrder", "style", "chrome", "overlay", "thumbnailMediaId",
    };

    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    public static string SpriteKey(long themeId, string indexSha256, string file) =>
        "themes/" + themeId.ToString(CultureInfo.InvariantCulture) + "/sprites/" + indexSha256 + "/" + file;

    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapCreate(app);
        MapPatch(app);
        MapSpriteTickets(app);
        MapSpriteConfirm(app);
        MapDefault(app);
        MapDelete(app);
    }

    // GET /admin/themes → 200 { items: TrackerTheme[] } by renderer, sortOrder, id.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/themes",
            async (WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<TrackerThemeDto>();
                await using var cmd = new NpgsqlCommand(SelectSql + " order by t.renderer, t.sort_order, t.id;", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) items.Add(ReadRow(reader, options));
                return Results.Ok(new ItemsResponse<TrackerThemeDto> { Items = items });
            })
            .WithTags(Tag)
            .Produces<ItemsResponse<TrackerThemeDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Themes)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/themes → 201 TrackerTheme. Validate, canonicalize, hash,
    // write the style object when absent, insert. Never snapshot-affecting.
    private static void MapCreate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/themes",
            async (HttpRequest request, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit,
                   WmsfoConnectionStrings connections, WmsfoOptions options, IObjectStore store,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                var root = await ReadBodyAsync(request, ct);
                var v = new RequestValidation();
                foreach (var p in root.EnumerateObject())
                {
                    if (!CreateFields.Contains(p.Name)) v.Field(p.Name, "is not a theme field");
                }
                string? renderer = null;
                if (!root.TryGetProperty("renderer", out var rendererEl)
                    || rendererEl.ValueKind != JsonValueKind.String
                    || rendererEl.GetString() is not (ThemeStyleValidator.Google or ThemeStyleValidator.MapLibre))
                    v.Field("renderer", "must be google or maplibre");
                else
                    renderer = rendererEl.GetString();
                var fields = ReadFields(root, create: true, renderer, v);
                v.ThrowIfInvalid();
                var email = AdminHelpers.RequireAdminEmail(ctx);

                await using (var conn = new NpgsqlConnection(connections.App))
                {
                    await conn.OpenAsync(ct);
                    await RequireKeyFreeAsync(conn, renderer!, fields.Key!, null, ct);
                    if (fields.Thumbnail is Guid thumb) await RequireThumbnailAsync(conn, null, thumb, ct);
                }

                var style = ThemeStyleValidator.Rewrite(renderer!, fields.Style!.Value, options.CdnBaseUrl, spriteBase: null);
                var sha = CanonicalJson.Sha256Hex(style);
                await EnsureStyleObjectAsync(store, sha, style, loggers.CreateLogger("Wmsfo.Api.Themes"), ct);

                var dto = await snap.RunWithoutSnapshotAsync<TrackerThemeDto>(async (conn, tx, token) =>
                {
                    if (fields.Thumbnail is Guid thumb) await RequireThumbnailAsync(conn, tx, thumb, token);
                    long id;
                    await using (var insert = new NpgsqlCommand(@"
insert into tracker_theme (renderer, key, name, sort_order, style_sha256, style_bytes, chrome, overlay,
                           thumbnail_media_id, created_by, updated_by)
values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $10)
returning id;", conn, tx))
                    {
                        insert.Parameters.Add(Text(renderer!));
                        insert.Parameters.Add(Text(fields.Key!));
                        insert.Parameters.Add(Text(fields.Name!));
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = fields.SortOrder ?? 0 });
                        insert.Parameters.Add(Text(sha));
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = style.Length });
                        insert.Parameters.Add(Jsonb(fields.Chrome!));
                        insert.Parameters.Add(Jsonb(fields.Overlay!));
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)fields.Thumbnail ?? DBNull.Value });
                        insert.Parameters.Add(Text(email));
                        id = (long)(await insert.ExecuteScalarAsync(token) ?? 0L);
                    }
                    var created = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    created.Audit = await audit.RecordAsync(conn, tx, "create", AuditEntity, IdText(id),
                        before: null, after: created, token);
                    return created;
                }, ct);
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
            })
            .WithTags(Tag)
            .Accepts<CreateTrackerThemeRequest>("application/json", "multipart/form-data")
            .Produces<TrackerThemeDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.ThemeWrite)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Themes)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PATCH /admin/themes/{id} → 200 TrackerTheme. Any field but renderer; a
    // new style writes a new object and leaves the old one.
    private static void MapPatch(IEndpointRouteBuilder app)
    {
        app.MapPatch("/admin/themes/{id:long}",
            async (long id, HttpRequest request, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit,
                   WmsfoConnectionStrings connections, WmsfoOptions options, IObjectStore store,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                var root = await ReadBodyAsync(request, ct);
                ThemeCore core;
                ThemeFields fields;
                bool snapshot;
                await using (var conn = new NpgsqlConnection(connections.App))
                {
                    await conn.OpenAsync(ct);
                    core = await ReadCoreAsync(conn, null, id, ct) ?? throw NotFound();
                    var v = new RequestValidation();
                    foreach (var p in root.EnumerateObject())
                    {
                        if (p.Name == "renderer") v.Field("renderer", "is immutable");
                        else if (!CreateFields.Contains(p.Name)) v.Field(p.Name, "is not a theme field");
                    }
                    fields = ReadFields(root, create: false, core.Renderer, v);
                    v.ThrowIfInvalid();
                    if (fields.Key is not null) await RequireKeyFreeAsync(conn, core.Renderer, fields.Key, id, ct);
                    if (fields.Thumbnail is Guid thumb) await RequireThumbnailAsync(conn, null, thumb, ct);
                    snapshot = await OnCurrentEventAsync(conn, [id], ct);
                }
                var email = AdminHelpers.RequireAdminEmail(ctx);

                byte[]? style = null;
                string? sha = null;
                if (fields.Style is JsonElement styleEl)
                {
                    var spriteBase = core.SpriteSha256 is null
                        ? null
                        : ThemeStyleValidator.SpriteBase(options.CdnBaseUrl, id, core.SpriteSha256);
                    style = ThemeStyleValidator.Rewrite(core.Renderer, styleEl, options.CdnBaseUrl, spriteBase);
                    sha = CanonicalJson.Sha256Hex(style);
                    await EnsureStyleObjectAsync(store, sha, style, loggers.CreateLogger("Wmsfo.Api.Themes"), ct);
                }

                var dto = await RunFrameAsync<TrackerThemeDto>(snap, snapshot, async (conn, tx, token) =>
                {
                    await LockAsync(conn, tx, id, token);
                    var before = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    if (fields.Thumbnail is Guid thumb) await RequireThumbnailAsync(conn, tx, thumb, token);

                    var sets = new List<string>();
                    var parameters = new List<NpgsqlParameter>();
                    void Set(string column, NpgsqlParameter p)
                    {
                        parameters.Add(p);
                        sets.Add($"{column} = ${parameters.Count}");
                    }
                    if (fields.Key is not null) Set("key", Text(fields.Key));
                    if (fields.Name is not null) Set("name", Text(fields.Name));
                    if (fields.SortOrder is int sort) Set("sort_order", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = sort });
                    if (style is not null)
                    {
                        Set("style_sha256", Text(sha!));
                        Set("style_bytes", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = style.Length });
                    }
                    if (fields.Chrome is not null) Set("chrome", Jsonb(fields.Chrome));
                    if (fields.Overlay is not null) Set("overlay", Jsonb(fields.Overlay));
                    if (fields.ThumbnailSet)
                        Set("thumbnail_media_id", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)fields.Thumbnail ?? DBNull.Value });
                    Set("updated_by", Text(email));
                    sets.Add("updated_at = now()");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await using (var update = new NpgsqlCommand(
                        $"update tracker_theme set {string.Join(", ", sets)} where id = ${parameters.Count};", conn, tx))
                    {
                        foreach (var p in parameters) update.Parameters.Add(p);
                        await update.ExecuteNonQueryAsync(token);
                    }
                    var updated = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    updated.Audit = await audit.RecordAsync(conn, tx, "update", AuditEntity, IdText(id),
                        before, updated, token);
                    return updated;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags(Tag)
            .Accepts<PatchTrackerThemeRequest>("application/json", "multipart/form-data")
            .Produces<TrackerThemeDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.ThemeWrite)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Themes)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/themes/{id}/sprite → 201 SpriteTickets (200 with the same
    // tickets when the theme already carries the set). MapLibre only.
    private static void MapSpriteTickets(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/themes/{id:long}/sprite",
            async (long id, SpriteTicketsRequest body, HttpContext ctx, AdminSnapshotTransaction snap,
                   AuditRecorder audit, WmsfoOptions options, IObjectStore store, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                var index = body.IndexSha256 ?? "";
                var (tickets, already) = await snap.RunWithoutSnapshotAsync<(SpriteTicketsDto, bool)>(async (conn, tx, token) =>
                {
                    await LockAsync(conn, tx, id, token);
                    var core = await ReadCoreAsync(conn, tx, id, token) ?? throw NotFound();
                    var v = new RequestValidation();
                    if (core.Renderer != ThemeStyleValidator.MapLibre) v.Field("renderer", "sprites are for maplibre themes only");
                    if (!Sha256Pattern().IsMatch(index)) v.Field("indexSha256", "must be 64 lowercase hex characters");
                    v.ThrowIfInvalid();

                    var expiresAt = DateTimeOffset.UtcNow.Add(ObjectTags.PresignLifetime);
                    var dto = new SpriteTicketsDto { IndexSha256 = index };
                    foreach (var (file, contentType) in SpriteFiles)
                    {
                        dto.Uploads.Add(new SpriteUploadDto
                        {
                            File = file,
                            UploadUrl = store.PresignPut(SpriteKey(id, index, file), contentType, ObjectTags.Pending),
                            Method = "PUT",
                            Headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["Content-Type"] = contentType,
                                ["x-amz-tagging"] = ObjectTags.Pending,
                            },
                            ExpiresAt = expiresAt,
                        });
                    }
                    var before = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    await audit.RecordAsync(conn, tx, "sprite", AuditEntity, IdText(id),
                        before, new { indexSha256 = index, files = SpriteFiles.Select(f => f.File).ToArray() }, token);
                    return (dto, string.Equals(core.SpriteSha256, index, StringComparison.Ordinal));
                }, ct);
                return Results.Json(tickets, statusCode: already ? StatusCodes.Status200OK : StatusCodes.Status201Created);
            })
            .WithTags(Tag)
            .Accepts<SpriteTicketsRequest>("application/json")
            .Produces<SpriteTicketsDto>(StatusCodes.Status201Created)
            .Produces<SpriteTicketsDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Themes)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/themes/{id}/sprite/confirm → 200 TrackerTheme. Head and read
    // the four files, check them, remove the pending tags, then set
    // sprite_sha256 and rewrite the style body with the new sprite base.
    private static void MapSpriteConfirm(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/themes/{id:long}/sprite/confirm",
            async (long id, SpriteConfirmRequest body, HttpContext ctx, AdminSnapshotTransaction snap,
                   AuditRecorder audit, WmsfoConnectionStrings connections, WmsfoOptions options,
                   IObjectStore store, ILoggerFactory loggers, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                var logger = loggers.CreateLogger("Wmsfo.Api.Themes");
                var index = body.IndexSha256 ?? "";
                ThemeCore core;
                bool snapshot;
                await using (var conn = new NpgsqlConnection(connections.App))
                {
                    await conn.OpenAsync(ct);
                    core = await ReadCoreAsync(conn, null, id, ct) ?? throw NotFound();
                    var v = new RequestValidation();
                    if (core.Renderer != ThemeStyleValidator.MapLibre) v.Field("renderer", "sprites are for maplibre themes only");
                    if (!Sha256Pattern().IsMatch(index)) v.Field("indexSha256", "must be 64 lowercase hex characters");
                    v.ThrowIfInvalid();
                    snapshot = await OnCurrentEventAsync(conn, [id], ct);
                }

                if (string.Equals(core.SpriteSha256, index, StringComparison.Ordinal))
                {
                    var same = await snap.RunWithoutSnapshotAsync<TrackerThemeDto>(async (conn, tx, token) =>
                    {
                        var dto = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                        dto.Audit = await audit.RecordAsync(conn, tx, "sprite", AuditEntity, IdText(id), dto, dto, token);
                        return dto;
                    }, ct);
                    return Results.Ok(same);
                }

                var keys = SpriteFiles.Select(f => SpriteKey(id, index, f.File)).ToArray();
                for (var i = 0; i < keys.Length; i++)
                {
                    var head = await ObjectCallAsync(() => store.HeadObjectAsync(keys[i], ct), logger, ct);
                    if (head is null) throw UploadNotFound(SpriteFiles[i].File);
                }

                byte[]? index1x = null;
                for (var i = 0; i < keys.Length; i++)
                {
                    var (file, contentType) = SpriteFiles[i];
                    var got = await ObjectCallAsync(() => store.GetObjectAsync(keys[i], ct), logger, ct)
                        ?? throw UploadNotFound(file);
                    var reason = CheckSpriteFile(contentType, got.Bytes, out var canonical);
                    if (reason is not null)
                    {
                        await DeleteQuietlyAsync(store, keys, logger);
                        throw FileInvalid(file, reason);
                    }
                    if (file == "sprite.json") index1x = canonical;
                }
                if (!string.Equals(CanonicalJson.Sha256Hex(index1x!), index, StringComparison.Ordinal))
                {
                    await DeleteQuietlyAsync(store, keys, logger);
                    RequestValidation.Throw("indexSha256", "the canonical bytes of sprite.json do not hash to it");
                }
                foreach (var key in keys)
                {
                    await ObjectCallAsync(async () => { await store.DeleteObjectTaggingAsync(key, ct); return true; }, logger, ct);
                }

                var current = await ObjectCallAsync(
                    () => store.GetObjectAsync(ThemeStyles.ObjectKey(core.StyleSha256), ct), logger, ct)
                    ?? throw new ApiException(StatusCodes.Status502BadGateway, ApiErrorCodes.UpstreamFailed,
                        "the theme's style object is missing");
                byte[] style;
                using (var doc = JsonDocument.Parse(current.Bytes))
                {
                    style = ThemeStyleValidator.Rewrite(core.Renderer, doc.RootElement, options.CdnBaseUrl,
                        ThemeStyleValidator.SpriteBase(options.CdnBaseUrl, id, index));
                }
                var sha = CanonicalJson.Sha256Hex(style);
                await EnsureStyleObjectAsync(store, sha, style, logger, ct);

                var result = await RunFrameAsync<TrackerThemeDto>(snap, snapshot, async (conn, tx, token) =>
                {
                    await LockAsync(conn, tx, id, token);
                    var before = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    await using (var update = new NpgsqlCommand(@"
update tracker_theme
set sprite_sha256 = $1, style_sha256 = $2, style_bytes = $3, updated_by = $4, updated_at = now()
where id = $5;", conn, tx))
                    {
                        update.Parameters.Add(Text(index));
                        update.Parameters.Add(Text(sha));
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = style.Length });
                        update.Parameters.Add(Text(email));
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await update.ExecuteNonQueryAsync(token);
                    }
                    var updated = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    updated.Audit = await audit.RecordAsync(conn, tx, "sprite", AuditEntity, IdText(id),
                        before, updated, token);
                    return updated;
                }, ct);
                return Results.Ok(result);
            })
            .WithTags(Tag)
            .Accepts<SpriteConfirmRequest>("application/json")
            .Produces<TrackerThemeDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Themes)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/themes/{id}/default → 200 TrackerTheme. True runs the
    // clear-then-set pair of sql.md 4.2 on the renderer; false clears the flag
    // on this theme. A collision on a default index retries once.
    private static void MapDefault(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/themes/{id:long}/default",
            async (long id, SetThemeDefaultRequest body, HttpContext ctx, AdminSnapshotTransaction snap,
                   AuditRecorder audit, WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                if (body.Light is null && body.Dark is null)
                    RequestValidation.Throw("light", "light or dark is required");
                var email = AdminHelpers.RequireAdminEmail(ctx);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        var dto = await RunDefaultOnceAsync(id, body, email, snap, audit, connections, options, ct);
                        return Results.Ok(dto);
                    }
                    catch (PostgresException ex) when (ex.SqlState == "23505"
                        && ex.ConstraintName is ConstraintErrorMapping.TrackerThemeOneDefaultLight
                            or ConstraintErrorMapping.TrackerThemeOneDefaultDark
                        && attempt < 1)
                    {
                        // The loser of a concurrent move sees the winner's flag on the retry.
                    }
                }
            })
            .WithTags(Tag)
            .Accepts<SetThemeDefaultRequest>("application/json")
            .Produces<TrackerThemeDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Themes)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static async Task<TrackerThemeDto> RunDefaultOnceAsync(
        long id, SetThemeDefaultRequest body, string email, AdminSnapshotTransaction snap, AuditRecorder audit,
        WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct)
    {
        bool snapshot;
        await using (var conn = new NpgsqlConnection(connections.App))
        {
            await conn.OpenAsync(ct);
            var core = await ReadCoreAsync(conn, null, id, ct) ?? throw NotFound();
            var involved = new List<long> { id };
            await using (var holders = new NpgsqlCommand(@"
select id from tracker_theme
where renderer = $1 and id <> $2 and ((default_light_mode and $3) or (default_dark_mode and $4));", conn))
            {
                holders.Parameters.Add(Text(core.Renderer));
                holders.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                holders.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = body.Light == true });
                holders.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = body.Dark == true });
                await using var reader = await holders.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) involved.Add(reader.GetInt64(0));
            }
            snapshot = await OnCurrentEventAsync(conn, involved, ct);
        }

        return await RunFrameAsync<TrackerThemeDto>(snap, snapshot, async (conn, tx, token) =>
        {
            await LockAsync(conn, tx, id, token);
            var before = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
            async Task ApplyFlagAsync(string column, bool? value)
            {
                if (value is null) return;
                if (value.Value)
                {
                    await TrackerThemeImpactQueries.MoveFlagAsync(conn, tx, column, id, email, token);
                    return;
                }
                await using var clear = new NpgsqlCommand(
                    $"update tracker_theme set {column} = false, updated_by = $2, updated_at = now() where id = $1;", conn, tx);
                clear.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                clear.Parameters.Add(Text(email));
                await clear.ExecuteNonQueryAsync(token);
            }
            await ApplyFlagAsync("default_light_mode", body.Light);
            await ApplyFlagAsync("default_dark_mode", body.Dark);
            var updated = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
            updated.Audit = await audit.RecordAsync(conn, tx, "default", AuditEntity, IdText(id),
                before, updated, token);
            return updated;
        }, ct);
    }

    // DELETE /admin/themes/{id} → 204. Optional { replacementId }. Preview,
    // refuse the last Google theme of an event without a google replacement,
    // apply, record before = { ...dto, impact }; after commit delete every
    // object under themes/{id}/ (the style object stays).
    private static void MapDelete(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/themes/{id:long}",
            async (long id, HttpRequest request, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit,
                   WmsfoConnectionStrings connections, WmsfoOptions options, IObjectStore store,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                var replacementId = await ReadDeleteBodyAsync(request, ct);
                bool snapshot;
                await using (var conn = new NpgsqlConnection(connections.App))
                {
                    await conn.OpenAsync(ct);
                    _ = await ReadCoreAsync(conn, null, id, ct) ?? throw NotFound();
                    if (replacementId is long r)
                        await TrackerThemeImpactQueries.RequireReplacementAsync(conn, null, id, r, ct);
                    snapshot = await OnCurrentEventAsync(conn,
                        replacementId is long rr ? [id, rr] : [id], ct);
                }

                await RunFrameAsync<object?>(snap, snapshot, async (conn, tx, token) =>
                {
                    await LockAsync(conn, tx, id, token);
                    var before = await ReadByIdAsync(conn, tx, id, options, token) ?? throw NotFound();
                    if (replacementId is long r)
                        await TrackerThemeImpactQueries.RequireReplacementAsync(conn, tx, id, r, token);
                    var impact = await TrackerThemeImpactQueries.PreviewAsync(conn, tx, id, token);
                    if (impact.Blocked is not null && replacementId is null)
                        throw new ApiException(StatusCodes.Status409Conflict, "last_google_theme", impact.Blocked);
                    await TrackerThemeImpactQueries.ApplyAsync(conn, tx, id, replacementId, email, token);
                    await audit.RecordAsync(conn, tx, "delete", AuditEntity, IdText(id),
                        before: ImpactBefore.Combine(before, impact), after: null, token);
                    return null;
                }, ct);

                var logger = loggers.CreateLogger("Wmsfo.Api.Themes");
                try
                {
                    var keys = new List<string>();
                    await foreach (var entry in store.ListPrefixAsync("themes/" + IdText(id) + "/", ct))
                        keys.Add(entry.Key);
                    await DeleteQuietlyAsync(store, keys, logger);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "theme object cleanup failed themeId={ThemeId}", id);
                }
                return Results.NoContent();
            })
            .WithTags(Tag)
            .Accepts<DeleteTrackerThemeRequest>(isOptional: true, "application/json")
            .Produces(StatusCodes.Status204NoContent)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Themes)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- request bodies ---

    // The create and patch body as one JSON object: the JSON body itself, or
    // the multipart form with `style` read from its file part (an unparsable
    // part is 400 at style), `chrome` and `overlay` parsed from their fields,
    // and `sortOrder` read as an integer.
    private static async Task<JsonElement> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        var contentType = request.ContentType ?? "";
        if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            var form = await request.ReadFormAsync(ct);
            var obj = new JsonObject();
            var v = new RequestValidation();
            foreach (var (name, values) in form)
            {
                var text = values.ToString();
                switch (name)
                {
                    case "style" or "chrome" or "overlay":
                        obj[name] = ParseOrFlag(Encoding.UTF8.GetBytes(text), name, v);
                        break;
                    case "sortOrder":
                        obj[name] = int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                            ? JsonValue.Create(n) : JsonValue.Create(text);
                        break;
                    case "thumbnailMediaId":
                        obj[name] = text.Length == 0 || text == "null" ? null : JsonValue.Create(text);
                        break;
                    default:
                        obj[name] = JsonValue.Create(text);
                        break;
                }
            }
            foreach (var file in form.Files)
            {
                if (file.Name != "style")
                {
                    v.Field(file.Name, "only style may be a file part");
                    continue;
                }
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                obj["style"] = ParseOrFlag(ms.ToArray(), "style", v);
            }
            v.ThrowIfInvalid();
            using var doc = JsonDocument.Parse(obj.ToJsonString());
            return doc.RootElement.Clone();
        }
        if (contentType.Length == 0 || contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
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
        throw new ApiException(StatusCodes.Status415UnsupportedMediaType, ApiErrorCodes.UnsupportedMediaType,
            "send application/json or multipart/form-data");
    }

    private static JsonNode? ParseOrFlag(byte[] bytes, string field, RequestValidation v)
    {
        try
        {
            return JsonNode.Parse(bytes);
        }
        catch (JsonException)
        {
            v.Field(field, "is not parsable JSON");
            return null;
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
            var body = JsonSerializer.Deserialize<DeleteTrackerThemeRequest>(text, CanonicalJson.Options);
            return body?.ReplacementId;
        }
        catch (JsonException)
        {
            RequestValidation.Throw("replacementId", "the body must be { replacementId } with a theme id or null");
            return null; // unreachable
        }
    }

    // The fields of a create or patch body that passed their rules. Null
    // means absent (patch) or invalid (already reported).
    private sealed class ThemeFields
    {
        public string? Key { get; set; }
        public string? Name { get; set; }
        public int? SortOrder { get; set; }
        public JsonElement? Style { get; set; }
        public string? Chrome { get; set; }
        public string? Overlay { get; set; }
        public bool ThumbnailSet { get; set; }
        public Guid? Thumbnail { get; set; }
    }

    // renderer is the theme's renderer (null when the create's is invalid, in
    // which case the style is not checked).
    private static ThemeFields ReadFields(JsonElement root, bool create, string? renderer, RequestValidation v)
    {
        var f = new ThemeFields();
        if (root.TryGetProperty("key", out var key))
        {
            if (key.ValueKind != JsonValueKind.String || !KeyPattern().IsMatch(key.GetString()!))
                v.Field("key", "must match ^[a-z][a-z0-9-]{1,39}$");
            else f.Key = key.GetString();
        }
        else if (create) v.Field("key", "is required");

        if (root.TryGetProperty("name", out var name))
        {
            var text = name.ValueKind == JsonValueKind.String ? name.GetString()!.Trim() : null;
            if (text is null || text.Length is < 1 or > 60) v.Field("name", "must be 1 to 60 characters");
            else f.Name = text;
        }
        else if (create) v.Field("name", "is required");

        if (root.TryGetProperty("sortOrder", out var sort))
        {
            if (sort.ValueKind == JsonValueKind.Number && sort.TryGetInt32(out var n)) f.SortOrder = n;
            else if (!(create && sort.ValueKind == JsonValueKind.Null)) v.Field("sortOrder", "must be an integer");
        }

        if (root.TryGetProperty("style", out var style))
        {
            if (renderer is not null)
            {
                var reasons = ThemeStyleValidator.Validate(renderer, style);
                if (reasons.Count > 0) v.Field("style", string.Join("; ", reasons));
                else f.Style = style.Clone();
            }
        }
        else if (create) v.Field("style", "is required");

        if (root.TryGetProperty("chrome", out var chrome))
        {
            var failures = ChromeContrast.ValidateChrome(chrome);
            foreach (var (path, reason) in failures) v.Field(path, reason);
            if (failures.Count == 0)
                f.Chrome = JsonSerializer.Serialize(ChromeContrast.ToChrome(chrome), CanonicalJson.Options);
        }
        else if (create) v.Field("chrome", "is required");

        if (root.TryGetProperty("overlay", out var overlay))
        {
            var failures = ChromeContrast.ValidateOverlay(overlay);
            foreach (var (path, reason) in failures) v.Field(path, reason);
            if (failures.Count == 0)
                f.Overlay = JsonSerializer.Serialize(ChromeContrast.ToOverlay(overlay), CanonicalJson.Options);
        }
        else if (create) v.Field("overlay", "is required");

        if (root.TryGetProperty("thumbnailMediaId", out var thumb))
        {
            if (thumb.ValueKind == JsonValueKind.Null)
            {
                f.ThumbnailSet = true;
            }
            else if (thumb.ValueKind == JsonValueKind.String && Guid.TryParse(thumb.GetString(), out var g))
            {
                f.ThumbnailSet = true;
                f.Thumbnail = g;
            }
            else v.Field("thumbnailMediaId", "must be a media asset id or null");
        }
        return f;
    }

    // --- checks ---

    private static async Task RequireKeyFreeAsync(
        NpgsqlConnection conn, string renderer, string key, long? exceptId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select 1 from tracker_theme where renderer = $1 and key = $2 and id <> $3;", conn);
        cmd.Parameters.Add(Text(renderer));
        cmd.Parameters.Add(Text(key));
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = exceptId ?? 0L });
        if (await cmd.ExecuteScalarAsync(ct) is not null)
            RequestValidation.Throw("key", "another theme of the renderer has this key");
    }

    // A thumbnail is a ready raster media asset: 404 when missing, 409
    // media_not_ready when not ready, 400 when not a raster.
    private static async Task RequireThumbnailAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid mediaId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select state, kind from media_asset where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
        var state = reader.GetString(0);
        var kind = reader.GetString(1);
        if (state != "ready")
            throw new ApiException(StatusCodes.Status409Conflict, "media_not_ready", "media asset is not ready");
        if (kind != "raster")
            RequestValidation.Throw("thumbnailMediaId", "must be a raster media asset");
    }

    // The [snapshot] rule of every theme write: the current event enables one
    // of the themes.
    private static async Task<bool> OnCurrentEventAsync(NpgsqlConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select exists (
  select 1 from event_tracker_theme et join event e on e.id = et.event_id
  where e.is_current and et.theme_id = any($1));", conn);
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

    // Returns the failure reason of one sprite file, or null; canonical is
    // the canonical bytes of a JSON file.
    private static string? CheckSpriteFile(string contentType, byte[] bytes, out byte[]? canonical)
    {
        canonical = null;
        if (contentType == PngContentType)
        {
            if (bytes.Length > SpritePngMaxBytes) return "must be at most 8 MB";
            if (bytes.Length < PngSignature.Length || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
                return "does not start with the PNG signature";
            return null;
        }
        if (bytes.Length > SpriteJsonMaxBytes) return "must be at most 1 MB";
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "must parse to a JSON object";
            canonical = ThemeStyles.Canonicalize(doc.RootElement);
            return null;
        }
        catch (JsonException)
        {
            return "is not parsable JSON";
        }
    }

    // --- object store ---

    // HEAD themes/{sha}.json and PUT it with the immutable header when absent;
    // 3 s per call, one attempt; a failure is 502 upstream_failed.
    private static async Task EnsureStyleObjectAsync(
        IObjectStore store, string sha, byte[] bytes, ILogger logger, CancellationToken ct)
    {
        var key = ThemeStyles.ObjectKey(sha);
        try
        {
            ObjectHead? head;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(ThemeStyles.ObjectCallTimeout);
                head = await store.HeadObjectAsync(key, cts.Token);
            }
            if (head is not null) return;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(ThemeStyles.ObjectCallTimeout);
                await store.PutObjectAsync(key, bytes, ThemeStyles.ContentType, ThemeStyles.ImmutableCacheControl,
                    tag: null, cts.Token);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "theme style write failed key={Key}", key);
            throw new ApiException(StatusCodes.Status502BadGateway, ApiErrorCodes.UpstreamFailed,
                "the style object write failed");
        }
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
            logger.LogWarning(ex, "theme object call failed");
            throw new ApiException(StatusCodes.Status502BadGateway, ApiErrorCodes.UpstreamFailed,
                "an object store call failed");
        }
    }

    private static async Task DeleteQuietlyAsync(IObjectStore store, IEnumerable<string> keys, ILogger logger)
    {
        foreach (var key in keys)
        {
            try { await store.DeleteObjectAsync(key, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "theme object delete failed key={Key}", key); }
        }
    }

    // --- rows ---

    private sealed record ThemeCore(string Renderer, string StyleSha256, string? SpriteSha256);

    private static async Task<ThemeCore?> ReadCoreAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select renderer, style_sha256, sprite_sha256 from tracker_theme where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ThemeCore(reader.GetString(0), reader.GetString(1).Trim(),
            reader.IsDBNull(2) ? null : reader.GetString(2).Trim());
    }

    private static async Task LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select 1 from tracker_theme where id = $1 for update;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        if (await cmd.ExecuteScalarAsync(ct) is null) throw NotFound();
    }

    private const string SelectSql = @"
select t.id, t.renderer, t.key, t.name, t.sort_order, t.style_sha256, t.style_bytes, t.sprite_sha256,
       t.chrome::text, t.overlay::text, t.thumbnail_media_id, t.default_light_mode, t.default_dark_mode,
       (select count(*)::int from event_tracker_theme et where et.theme_id = t.id),
       t.created_by, t.created_at, t.updated_by, t.updated_at,
       a.action, a.actor, a.at
from tracker_theme t
left join lateral (
  select action, actor, at from audit_log
  where entity = 'tracker_theme' and entity_id = t.id::text
  order by id desc limit 1
) a on true";

    private static async Task<TrackerThemeDto?> ReadByIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, WmsfoOptions options, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(SelectSql + " where t.id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRow(reader, options) : null;
    }

    private static TrackerThemeDto ReadRow(NpgsqlDataReader reader, WmsfoOptions options)
    {
        var cdn = options.CdnBaseUrl.TrimEnd('/');
        var id = reader.GetInt64(0);
        var styleSha = reader.GetString(5).Trim();
        var spriteSha = reader.IsDBNull(7) ? null : reader.GetString(7).Trim();
        var dto = new TrackerThemeDto
        {
            Id = id,
            Renderer = reader.GetString(1),
            Key = reader.GetString(2),
            Name = reader.GetString(3),
            SortOrder = reader.GetInt32(4),
            StyleUrl = cdn + "/" + ThemeStyles.ObjectKey(styleSha),
            StyleSha256 = styleSha,
            StyleBytes = reader.GetInt32(6),
            SpriteSha256 = spriteSha,
            SpriteUrl = spriteSha is null ? null : ThemeStyleValidator.SpriteBase(cdn, id, spriteSha),
            Chrome = JsonSerializer.Deserialize<Chrome>(reader.GetString(8), CanonicalJson.Options)!,
            Overlay = JsonSerializer.Deserialize<Overlay>(reader.GetString(9), CanonicalJson.Options)!,
            ThumbnailMediaId = reader.IsDBNull(10) ? null : reader.GetGuid(10).ToString(),
            DefaultLightMode = reader.GetBoolean(11),
            DefaultDarkMode = reader.GetBoolean(12),
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

    private static NpgsqlParameter Text(string value) => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter Jsonb(string json) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json };

    private static string IdText(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "theme not found");

    private static ApiException UploadNotFound(string file) =>
        new(StatusCodes.Status404NotFound, "upload_not_found", $"{file} was not uploaded", new UploadFileDetails(file));

    private static ApiException FileInvalid(string file, string reason) =>
        new(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, $"file {file} {reason}",
            new FileValidationDetails(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["file"] = $"{file} {reason}" }, file));
}

// details of 404 upload_not_found on a sprite confirm: the missing file.
public sealed record UploadFileDetails(string File);

// details of 400 validation_failed at `file` on a sprite confirm: the fields
// map and the file that failed.
public sealed record FileValidationDetails(IReadOnlyDictionary<string, string> Fields, string File);
