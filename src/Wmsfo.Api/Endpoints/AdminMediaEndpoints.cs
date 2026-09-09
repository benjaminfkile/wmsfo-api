using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;
using Wmsfo.Api.Media;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints;

// api.md 11.2 / 11.3 / 11.5, contracts 4.5 Media. Presigned PUT, confirm with
// sniff + SVG validation + ImageSharp decode + variants, list with filters and
// search, PATCH (alt/title) as a [snapshot] frame, DELETE with the usage guard.
public static class AdminMediaEndpoints
{
    // Type limits per api.md 11.2. Raster and gif 20 MB, svg 1 MB.
    public const long RasterOrGifLimit = 20L * 1024 * 1024;
    public const long SvgLimit = 1L * 1024 * 1024;
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    public const string WebpContentType = "image/webp";
    public static readonly TimeSpan PutTimeout = TimeSpan.FromSeconds(3);

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.Ordinal)
    {
        ImageSniffer.Png, ImageSniffer.Jpeg, ImageSniffer.Webp, ImageSniffer.Gif, ImageSniffer.Svg,
    };

    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapUploadUrl(app);
        MapConfirm(app);
        MapGet(app);
        MapUsage(app);
        MapPatch(app);
        MapDelete(app);
    }

    // GET /admin/media?cursor=&limit=&kind=&state=&q= - newest first with the
    // documented filters. Cursor is base64url of the last id (contracts 4.0).
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/media",
            async (HttpContext ctx, WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                var cursorRaw = ctx.Request.Query["cursor"].ToString();
                var limitRaw = ctx.Request.Query["limit"].ToString();
                var kindFilter = ctx.Request.Query["kind"].ToString();
                var stateFilter = ctx.Request.Query["state"].ToString();
                var q = ctx.Request.Query["q"].ToString();

                var v = new RequestValidation();
                if (!string.IsNullOrEmpty(kindFilter) && kindFilter is not ("raster" or "svg" or "gif"))
                    v.Field("kind", "must be raster, svg, or gif");
                if (!string.IsNullOrEmpty(stateFilter) && stateFilter is not ("pending" or "ready" or "orphaned"))
                    v.Field("state", "must be pending, ready, or orphaned");
                v.ThrowIfInvalid();

                int limit = 50;
                if (!string.IsNullOrEmpty(limitRaw))
                {
                    if (!int.TryParse(limitRaw, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out limit) || limit <= 0)
                        RequestValidation.Throw("limit", "must be a positive integer");
                    if (limit > 500) limit = 500;
                }
                Guid? cursor = DecodeUuidCursor(string.IsNullOrEmpty(cursorRaw) ? null : cursorRaw);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<MediaAssetDto>();
                var sql = new System.Text.StringBuilder(@"
select id, filename, content_type, kind, state, s3_key, size_bytes, width, height, sha256,
       variants, alt, title, uploaded_by, created_at, confirmed_at, unreferenced_since, orphaned_at
from media_asset
where 1 = 1");
                var parameters = new List<NpgsqlParameter>();
                var next = 1;
                if (!string.IsNullOrEmpty(kindFilter))
                {
                    sql.Append($" and kind = ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = kindFilter });
                }
                if (!string.IsNullOrEmpty(stateFilter))
                {
                    sql.Append($" and state = ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = stateFilter });
                }
                if (!string.IsNullOrEmpty(q))
                {
                    sql.Append($" and (filename ilike ${next} or title ilike ${next} or alt ilike ${next})");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "%" + q + "%" });
                    next++;
                }
                if (cursor is not null)
                {
                    sql.Append($@" and (created_at, id) < (
  select created_at, id from media_asset where id = ${next}
)");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = cursor.Value });
                    next++;
                }
                sql.Append(" order by created_at desc, id desc");
                sql.Append($" limit ${next};");
                parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit + 1 });

                await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
                foreach (var p in parameters) cmd.Parameters.Add(p);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(ReadMediaRow(reader, options));
                }

                string? nextCursor = null;
                if (items.Count > limit)
                {
                    var last = items[limit - 1];
                    nextCursor = EncodeUuidCursor(last.Id);
                    items.RemoveAt(items.Count - 1);
                }
                return Results.Ok(new PageResponse<MediaAssetDto> { Items = items, NextCursor = nextCursor });
            })
            .WithTags("AdminMedia")
            .Produces<PageResponse<MediaAssetDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/media/upload-url - sanitize filename, mint id, insert pending,
    // presign PUT (15 minutes) with Content-Type and x-amz-tagging signed.
    private static void MapUploadUrl(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/media/upload-url",
            async (MediaUploadUrlRequest body, HttpContext ctx, WmsfoConnectionStrings connections,
                   IObjectStore store, WmsfoOptions options, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (string.IsNullOrWhiteSpace(body.ContentType) || !AllowedContentTypes.Contains(body.ContentType))
                    v.Field("contentType", "must be image/png, image/jpeg, image/webp, image/gif, or image/svg+xml");
                if (string.IsNullOrWhiteSpace(body.Filename))
                    v.Field("filename", "must be 1 to 100 characters");
                if (body.Alt is null || body.Alt.Length > 500)
                    v.Field("alt", "must be 0 to 500 characters");
                if (body.Title is null || body.Title.Length > 200)
                    v.Field("title", "must be 0 to 200 characters");
                v.ThrowIfInvalid();

                var email = AdminHelpers.RequireAdminEmail(ctx);
                var sanitized = FilenameSanitizer.Sanitize(body.Filename, body.ContentType);
                if (sanitized is null)
                    RequestValidation.Throw("filename", "extension must match contentType");

                var limit = LimitForContentType(body.ContentType);
                if (body.SizeBytes <= 0 || body.SizeBytes > limit)
                    throw new ApiException(StatusCodes.Status413PayloadTooLarge, ApiErrorCodes.PayloadTooLarge,
                        $"size {body.SizeBytes} exceeds {limit}-byte limit for {body.ContentType}");

                var id = Guid.NewGuid();
                var key = $"media/{id}/{sanitized}";
                var kind = KindFor(body.ContentType);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using (var ins = new NpgsqlCommand(@"
insert into media_asset (id, filename, content_type, kind, state, s3_key, size_bytes, alt, title, uploaded_by)
values ($1, $2, $3, $4, 'pending', $5, $6, $7, $8, $9);", conn))
                {
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = sanitized! });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.ContentType });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = kind });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.SizeBytes });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Alt });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Title });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                    await ins.ExecuteNonQueryAsync(ct);
                }

                var uploadUrl = store.PresignPut(key, body.ContentType, ObjectTags.Pending);
                var localHint = store is LocalObjectStore
                    ? $"?filename={Uri.EscapeDataString(sanitized!)}"
                    : "";
                if (localHint.Length > 0) uploadUrl += localHint;

                var media = await ReadByIdAsync(conn, null, id, options, ct)
                    ?? throw new InvalidOperationException("failed to reload created media row");
                return Results.Json(new UploadTicketDto
                {
                    Media = media,
                    UploadUrl = uploadUrl,
                    Method = "PUT",
                    Headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Content-Type"] = body.ContentType,
                        ["x-amz-tagging"] = ObjectTags.Pending,
                    },
                    ExpiresAt = DateTimeOffset.UtcNow.Add(ObjectTags.PresignLifetime),
                }, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminMedia")
            .Accepts<MediaUploadUrlRequest>("application/json")
            .Produces<UploadTicketDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.MediaUploadUrlPerPerson);
    }

    // POST /admin/media/{id}/confirm - the full confirm pipeline (api.md 11.3).
    private static void MapConfirm(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/media/{id}/confirm",
            async (string id, HttpContext ctx, WmsfoConnectionStrings connections,
                   IObjectStore store, WmsfoOptions options, ILoggerFactory loggerFactory, CancellationToken ct) =>
            {
                var logger = loggerFactory.CreateLogger("Wmsfo.Api.Media.Confirm");
                if (!Guid.TryParse(id, out var mediaId))
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                _ = AdminHelpers.RequireAdminEmail(ctx);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                // 1. Read the row.
                string? state = null, contentType = null, s3Key = null;
                long? declaredSize = null;
                await using (var read = new NpgsqlCommand(
                    "select state, content_type, s3_key, size_bytes from media_asset where id = $1;", conn))
                {
                    read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct))
                        throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                    state = reader.GetString(0);
                    contentType = reader.GetString(1);
                    s3Key = reader.GetString(2);
                    declaredSize = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                }
                if (!string.Equals(state, "pending", StringComparison.Ordinal))
                    throw new ApiException(StatusCodes.Status409Conflict, "media_not_pending", "media asset is not pending");

                var limit = LimitForContentType(contentType!);

                // 2. Head the object; missing = 404 upload_not_found. Enforce the
                //    hard type limit against the actual object length.
                var head = await store.HeadObjectAsync(s3Key!, ct);
                if (head is null)
                    throw new ApiException(StatusCodes.Status404NotFound, "upload_not_found", "upload not found");
                if (head.ContentLength > limit)
                {
                    await SafeDeleteAsync(store, s3Key!, ct);
                    await DeletePendingRowAsync(conn, mediaId, ct);
                    throw new ApiException(StatusCodes.Status413PayloadTooLarge, ApiErrorCodes.PayloadTooLarge,
                        $"upload {head.ContentLength} bytes exceeds {limit}-byte limit for {contentType}");
                }

                // 3. GET the object, sniff, validate, decode/variants.
                var got = await store.GetObjectAsync(s3Key!, ct)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, "upload_not_found", "upload not found");
                var bytes = got.Bytes;
                var sniffed = ImageSniffer.Detect(bytes);
                if (sniffed is null || !string.Equals(sniffed, contentType, StringComparison.Ordinal))
                {
                    await SafeDeleteAsync(store, s3Key!, ct);
                    await DeletePendingRowAsync(conn, mediaId, ct);
                    var v = new RequestValidation();
                    v.Field("file", sniffed is null ? "type not recognized" : "type does not match ticket");
                    v.ThrowIfInvalid();
                }

                int? width = null;
                int? height = null;
                var variantMap = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var variantKeysPut = new List<string>();

                if (string.Equals(contentType, ImageSniffer.Svg, StringComparison.Ordinal))
                {
                    var validation = SvgValidator.Validate(bytes);
                    if (!validation.IsValid)
                    {
                        await SafeDeleteAsync(store, s3Key!, ct);
                        await DeletePendingRowAsync(conn, mediaId, ct);
                        var v = new RequestValidation();
                        v.Field("file", $"svg rejected: {validation.Reason}");
                        v.ThrowIfInvalid();
                    }
                    // width/height stay null for svg.
                }
                else if (string.Equals(contentType, ImageSniffer.Gif, StringComparison.Ordinal))
                {
                    try
                    {
                        var (w, h) = VariantDeriver.DecodeGifDimensions(bytes);
                        width = w; height = h;
                    }
                    catch (MediaDecodeException ex)
                    {
                        await SafeDeleteAsync(store, s3Key!, ct);
                        await DeletePendingRowAsync(conn, mediaId, ct);
                        RequestValidation.Throw("file", $"decode failed: {ex.Reason}");
                    }
                }
                else
                {
                    // Raster (png, jpeg, webp).
                    RasterDecodeResult decoded;
                    try
                    {
                        decoded = VariantDeriver.DecodeRaster(bytes);
                    }
                    catch (MediaDecodeException ex)
                    {
                        await SafeDeleteAsync(store, s3Key!, ct);
                        await DeletePendingRowAsync(conn, mediaId, ct);
                        RequestValidation.Throw("file", $"decode failed: {ex.Reason}");
                        return Results.StatusCode(StatusCodes.Status500InternalServerError); // unreachable
                    }
                    width = decoded.Width;
                    height = decoded.Height;

                    // PUT each variant. Any failure: 502 media_write_failed; the
                    // row stays pending so a retry re-derives and re-PUTs.
                    foreach (var (target, webpBytes) in decoded.Variants)
                    {
                        var variantKey = $"media/{mediaId}/w{target}.webp";
                        try
                        {
                            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            cts.CancelAfter(PutTimeout);
                            await store.PutObjectAsync(
                                variantKey, webpBytes, WebpContentType, ImmutableCacheControl,
                                ObjectTags.Pending, cts.Token);
                            variantKeysPut.Add(variantKey);
                            variantMap[target.ToString(System.Globalization.CultureInfo.InvariantCulture)] = variantKey;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex,
                                "media variant PUT failed id={MediaId} width={Width}; marker={Marker}",
                                mediaId, target, LogMarkers.MediaWriteFailed);
                            throw new ApiException(StatusCodes.Status502BadGateway, "media_write_failed",
                                $"variant write failed for width {target}");
                        }
                    }
                }

                // 4. sha256 of the original bytes.
                var sha = CanonicalJson.Sha256Hex(bytes);

                // 5. Tag removal on the original and every variant.
                try
                {
                    await store.DeleteObjectTaggingAsync(s3Key!, ct);
                    foreach (var vk in variantKeysPut)
                    {
                        await store.DeleteObjectTaggingAsync(vk, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "media tag removal failed id={MediaId}; marker={Marker}",
                        mediaId, LogMarkers.MediaWriteFailed);
                    throw new ApiException(StatusCodes.Status502BadGateway, "media_write_failed",
                        "tag removal failed");
                }

                // 6. Update the row to ready.
                var variantsJson = JsonSerializer.Serialize(variantMap, CanonicalJson.Options);
                await using (var upd = new NpgsqlCommand(@"
update media_asset
set state = 'ready',
    size_bytes = $1,
    width = $2,
    height = $3,
    sha256 = $4,
    variants = $5::jsonb,
    confirmed_at = now()
where id = $6 and state = 'pending';", conn))
                {
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (long)head.ContentLength });
                    upd.Parameters.Add(width.HasValue
                        ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = width.Value }
                        : new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = DBNull.Value });
                    upd.Parameters.Add(height.HasValue
                        ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = height.Value }
                        : new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = DBNull.Value });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = sha });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = variantsJson });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
                    var rows = await upd.ExecuteNonQueryAsync(ct);
                    if (rows == 0)
                        throw new ApiException(StatusCodes.Status409Conflict, "media_not_pending", "media asset is not pending");
                }

                var dto = await ReadByIdAsync(conn, null, mediaId, options, ct)
                    ?? throw new InvalidOperationException("failed to reload media row after confirm");
                return Results.Ok(dto);
            })
            .WithTags("AdminMedia")
            .Produces<MediaAssetDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapGet(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/media/{id}",
            async (string id, WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                if (!Guid.TryParse(id, out var mediaId))
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var dto = await ReadByIdAsync(conn, null, mediaId, options, ct);
                if (dto is null)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                return Results.Ok(dto);
            })
            .WithTags("AdminMedia")
            .Produces<MediaAssetDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapUsage(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/media/{id}/usage",
            async (string id, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                if (!Guid.TryParse(id, out var mediaId))
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using (var check = new NpgsqlCommand("select 1 from media_asset where id = $1;", conn))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull)
                        throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                }
                var usage = await MediaUsage.ForAsync(conn, mediaId, ct);
                return Results.Ok(usage);
            })
            .WithTags("AdminMedia")
            .Produces<MediaUsageDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PATCH /admin/media/{id} - [snapshot] frame because alt rides in the
    // snapshot's media map (contracts 1.3).
    private static void MapPatch(IEndpointRouteBuilder app)
    {
        app.MapPatch("/admin/media/{id}",
            async (string id, MediaPatchRequest body, HttpContext ctx,
                   AdminSnapshotTransaction snapshotTx,
                   WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                if (!Guid.TryParse(id, out var mediaId))
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                var v = new RequestValidation();
                if (body.Alt is not null && body.Alt.Length > 500)
                    v.Field("alt", "must be 0 to 500 characters");
                if (body.Title is not null && body.Title.Length > 200)
                    v.Field("title", "must be 0 to 200 characters");
                v.ThrowIfInvalid();
                _ = AdminHelpers.RequireAdminEmail(ctx);

                var (result, _) = await snapshotTx.RunAsync(async (conn, tx, token) =>
                {
                    var sets = new List<string>();
                    var parameters = new List<NpgsqlParameter>();
                    var next = 1;
                    if (body.Alt is not null)
                    {
                        sets.Add($"alt = ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Alt });
                    }
                    if (body.Title is not null)
                    {
                        sets.Add($"title = ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Title });
                    }
                    if (sets.Count > 0)
                    {
                        var whereIdx = next;
                        var sql = $"update media_asset set {string.Join(", ", sets)} where id = ${whereIdx};";
                        await using var upd = new NpgsqlCommand(sql, conn, tx);
                        foreach (var p in parameters) upd.Parameters.Add(p);
                        upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
                        var rows = await upd.ExecuteNonQueryAsync(token);
                        if (rows == 0)
                            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                    }
                    else
                    {
                        // No-op patch - still confirm the row exists.
                        await using var check = new NpgsqlCommand("select 1 from media_asset where id = $1;", conn, tx);
                        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
                        var r = await check.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull)
                            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                    }
                    var reread = await ReadByIdAsync(conn, tx, mediaId, options, token)
                        ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                    return reread;
                }, ct);

                return Results.Ok(result);
            })
            .WithTags("AdminMedia")
            .Accepts<MediaPatchRequest>("application/json")
            .Produces<MediaAssetDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // DELETE /admin/media/{id} - 409 media_in_use with details.usage if referenced;
    // otherwise delete every object under media/{id}/, then the row.
    private static void MapDelete(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/media/{id}",
            async (string id, HttpContext ctx, WmsfoConnectionStrings connections, IObjectStore store,
                   ILoggerFactory loggerFactory, CancellationToken ct) =>
            {
                if (!Guid.TryParse(id, out var mediaId))
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                _ = AdminHelpers.RequireAdminEmail(ctx);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using (var check = new NpgsqlCommand("select 1 from media_asset where id = $1;", conn))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull)
                        throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                }

                var usage = await MediaUsage.ForAsync(conn, mediaId, ct);
                if (MediaUsage.IsInUse(usage))
                    throw new ApiException(StatusCodes.Status409Conflict, "media_in_use",
                        "media asset is referenced", new MediaInUseDetails(usage));

                var prefix = $"media/{mediaId}/";
                var keys = new List<string>();
                await foreach (var entry in store.ListPrefixAsync(prefix, ct))
                    keys.Add(entry.Key);

                foreach (var key in keys)
                {
                    try { await store.DeleteObjectAsync(key, ct); }
                    catch (Exception ex)
                    {
                        loggerFactory.CreateLogger("Wmsfo.Api.Endpoints.AdminMedia")
                            .LogWarning(ex, "media object delete failed; key={Key}", key);
                    }
                }

                await using (var del = new NpgsqlCommand("delete from media_asset where id = $1;", conn))
                {
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
                    await del.ExecuteNonQueryAsync(ct);
                }
                return Results.NoContent();
            })
            .WithTags("AdminMedia")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- helpers ---

    public static long LimitForContentType(string contentType) => contentType switch
    {
        ImageSniffer.Svg => SvgLimit,
        _ => RasterOrGifLimit,
    };

    public static string KindFor(string contentType) => contentType switch
    {
        ImageSniffer.Gif => "gif",
        ImageSniffer.Svg => "svg",
        _ => "raster",
    };

    public static async Task<MediaAssetDto?> ReadByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, WmsfoOptions options, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select id, filename, content_type, kind, state, s3_key, size_bytes, width, height, sha256,
       variants, alt, title, uploaded_by, created_at, confirmed_at, unreferenced_since, orphaned_at
from media_asset where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadMediaRow(reader, options);
    }

    private static MediaAssetDto ReadMediaRow(NpgsqlDataReader reader, WmsfoOptions options)
    {
        var cdn = options.CdnBaseUrl.TrimEnd('/');
        var s3Key = reader.GetString(5);
        var variantsJson = reader.GetString(10);
        var variants = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(variantsJson))
        {
            foreach (var e in doc.RootElement.EnumerateObject())
            {
                var value = e.Value.GetString() ?? "";
                variants[e.Name] = cdn + "/" + value;
            }
        }
        return new MediaAssetDto
        {
            Id = reader.GetGuid(0).ToString(),
            Filename = reader.GetString(1),
            ContentType = reader.GetString(2),
            Kind = reader.GetString(3),
            State = reader.GetString(4),
            SizeBytes = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            Width = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            Height = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            Sha256 = reader.IsDBNull(9) ? null : reader.GetString(9).Trim(),
            Alt = reader.GetString(11),
            Title = reader.GetString(12),
            Url = cdn + "/" + s3Key,
            Variants = variants,
            UploadedBy = reader.GetString(13),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(14),
            ConfirmedAt = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
            UnreferencedSince = reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
            OrphanedAt = reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
        };
    }

    private static async Task DeletePendingRowAsync(NpgsqlConnection conn, Guid id, CancellationToken ct)
    {
        await using var del = new NpgsqlCommand(
            "delete from media_asset where id = $1 and state = 'pending';", conn);
        del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
        await del.ExecuteNonQueryAsync(ct);
    }

    private static Guid? DecodeUuidCursor(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var padded = raw.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            if (!Guid.TryParse(text, out var id))
                throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "cursor is malformed");
            return id;
        }
        catch (ApiException) { throw; }
        catch
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "cursor is malformed");
        }
    }

    private static string EncodeUuidCursor(string id)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(id);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static async Task SafeDeleteAsync(IObjectStore store, string key, CancellationToken ct)
    {
        try { await store.DeleteObjectAsync(key, ct); }
        catch { }
    }
}

// details.usage: MediaUsage for the 409 media_in_use response.
public sealed record MediaInUseDetails(MediaUsageDto Usage);
