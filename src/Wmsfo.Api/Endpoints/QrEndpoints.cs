using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5a QR codes (Canvasser). Every write is [snapshot] except detach
// on a code with no open attachment (idempotent no-op) and delete which is
// Admin-only. Attach folds the last hour's unattached scans onto the new row
// (api.md 11b).
public static class QrEndpoints
{
    // A fixed list of markers that mark a user agent as a bot (api.md 11b).
    public static readonly IReadOnlyList<string> BotMarkers = new[]
    {
        "bot", "crawler", "spider", "preview", "facebookexternalhit",
        "Slackbot", "WhatsApp", "Twitterbot", "LinkedInBot", "HeadlessChrome",
    };

    public static bool IsBotUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return false;
        foreach (var marker in BotMarkers)
        {
            if (userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static string ComputeIpHash(string salt, string clientIp)
    {
        var input = System.Text.Encoding.UTF8.GetBytes((salt ?? "") + (clientIp ?? ""));
        var hash = System.Security.Cryptography.SHA256.HashData(input);
        return Convert.ToHexStringLower(hash);
    }

    // Tag formatting: "qr-" + zero-padded to 3 (or the natural width when it
    // exceeds three digits). api.md 11b.
    public static string FormatTag(int number)
    {
        var width = number < 1000 ? 3 : number.ToString(CultureInfo.InvariantCulture).Length;
        return "qr-" + number.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapMint(app);
        MapGet(app);
        MapPatch(app);
        MapAttach(app);
        MapDetach(app);
        MapDelete(app);
        MapScanBeacon(app);
    }

    // GET /admin/qr-codes → 200 { items: QrCode[] } by tag.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/qr-codes",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = await QrRead.ListAsync(conn, null, ct).ConfigureAwait(false);
                return Results.Ok(new ItemsResponse<QrCodeDto> { Items = items });
            })
            .WithTags("AdminQrCodes")
            .Produces<ItemsResponse<QrCodeDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/qr-codes { count } (1..100). Sequential tags, one batch,
    // qr_code locked for update against concurrent mints. Unattached, active,
    // opening the home page. api.md 11b.
    private static void MapMint(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/qr-codes",
            async (MintQrCodesRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, AuditRecorder audit, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (body.Count < 1 || body.Count > 100)
                    v.Field("count", "must be between 1 and 100");
                v.ThrowIfInvalid();
                var email = AdminHelpers.RequireAdminEmail(ctx);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var minted = new List<QrCodeDto>();
                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    // Lock qr_code table exclusively so a concurrent mint cannot
                    // pick the same batch number or the same tags.
                    await using (var lockCmd = new NpgsqlCommand(
                        "lock table qr_code in exclusive mode;", conn, tx))
                    {
                        await lockCmd.ExecuteNonQueryAsync(ct);
                    }
                    int nextNumber = 1;
                    int nextBatch = 1;
                    await using (var read = new NpgsqlCommand(@"
select coalesce(max(batch_no), 0) + 1,
       coalesce(max(cast(nullif(regexp_replace(tag, '^qr-0*', ''), '') as integer)), 0) + 1
from qr_code;", conn, tx))
                    {
                        await using var reader = await read.ExecuteReaderAsync(ct);
                        if (await reader.ReadAsync(ct))
                        {
                            nextBatch = reader.GetInt32(0);
                            nextNumber = reader.GetInt32(1);
                        }
                    }
                    var newIds = new List<long>();
                    for (var i = 0; i < body.Count; i++)
                    {
                        var tag = FormatTag(nextNumber + i);
                        await using var ins = new NpgsqlCommand(@"
insert into qr_code (tag, batch_no, active, note, created_by, updated_at)
values ($1, $2, true, '', $3, now()) returning id;", conn, tx);
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = tag });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = nextBatch });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                        var idObj = await ins.ExecuteScalarAsync(ct);
                        newIds.Add((long)(idObj ?? 0L));
                    }
                    foreach (var id in newIds)
                    {
                        var dto = await QrRead.ByIdAsync(conn, tx, id, ct);
                        if (dto is null) continue;
                        var stamp = await audit.RecordAsync(conn, tx, "create", "qr_code",
                            id.ToString(CultureInfo.InvariantCulture), before: null, after: dto, ct);
                        dto.Audit = stamp;
                        minted.Add(dto);
                    }
                    await tx.CommitAsync(ct);
                }
                return Results.Json(new ItemsResponse<QrCodeDto> { Items = minted },
                    statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminQrCodes")
            .Accepts<MintQrCodesRequest>("application/json")
            .Produces<ItemsResponse<QrCodeDto>>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // GET /admin/qr-codes/{id} → 200 QrCodeDetail.
    private static void MapGet(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/qr-codes/{id:long}",
            async (long id, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var dto = await QrRead.DetailAsync(conn, null, id, ct);
                if (dto is null) throw NotFound();
                return Results.Ok(dto);
            })
            .WithTags("AdminQrCodes")
            .Produces<QrCodeDetailDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PATCH /admin/qr-codes/{id} [snapshot]. Any of opensPageId, forwardUrl,
    // note, active. Body parsed with JsonElement so `null` and "absent" differ.
    private static void MapPatch(IEndpointRouteBuilder app)
    {
        app.MapPatch("/admin/qr-codes/{id:long}",
            async (long id, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit,
                   CancellationToken ct) =>
            {
                var raw = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body,
                    Wmsfo.Api.Objects.CanonicalJson.Options, ct);
                var patch = ParseQrPatch(raw);
                _ = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<QrCodeDto>(async (conn, tx, token) =>
                {
                    var before = await QrRead.ByIdAsync(conn, tx, id, token);
                    if (before is null) throw NotFound();
                    if (patch.HasOpensPageId && patch.OpensPageId is not null && patch.HasForwardUrl && !string.IsNullOrEmpty(patch.ForwardUrl))
                    {
                        RequestValidation.Throw("opensPageId", "opensPageId and forwardUrl cannot both be set");
                    }
                    // Validate a page id references an existing non-hidden page.
                    if (patch.HasOpensPageId && patch.OpensPageId is long pid)
                    {
                        await using var pageCheck = new NpgsqlCommand(
                            "select is_hidden from page where id = $1;", conn, tx);
                        pageCheck.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pid });
                        var r = await pageCheck.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull) throw NotFound();
                    }
                    var sets = new List<string>();
                    var parameters = new List<NpgsqlParameter>();
                    var next = 1;
                    if (patch.HasOpensPageId)
                    {
                        if (patch.OpensPageId is null) sets.Add("opens_page_id = null");
                        else { sets.Add($"opens_page_id = ${next++}"); parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = patch.OpensPageId.Value }); }
                    }
                    if (patch.HasForwardUrl)
                    {
                        if (patch.ForwardUrl is null) sets.Add("forward_url = null");
                        else
                        {
                            if (!IsHttpsUrl(patch.ForwardUrl)) RequestValidation.Throw("forwardUrl", "must be an https URL");
                            sets.Add($"forward_url = ${next++}");
                            parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = patch.ForwardUrl });
                        }
                    }
                    if (patch.Note is not null)
                    {
                        if (patch.Note.Length > 500) RequestValidation.Throw("note", "must be 0 to 500 characters");
                        sets.Add($"note = ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = patch.Note });
                    }
                    if (patch.Active is bool a)
                    {
                        sets.Add($"active = ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = a });
                    }
                    if (sets.Count > 0)
                    {
                        sets.Add("updated_at = now()");
                        var whereIdx = next;
                        await using var upd = new NpgsqlCommand(
                            $"update qr_code set {string.Join(", ", sets)} where id = ${whereIdx};", conn, tx);
                        foreach (var p in parameters) upd.Parameters.Add(p);
                        upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await upd.ExecuteNonQueryAsync(token);
                    }
                    // Verify the check constraint's post-condition: not both set.
                    await using (var check = new NpgsqlCommand(
                        "select opens_page_id, forward_url from qr_code where id = $1;", conn, tx))
                    {
                        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await using var reader = await check.ExecuteReaderAsync(token);
                        await reader.ReadAsync(token);
                        var hasPage = !reader.IsDBNull(0);
                        var hasUrl = !reader.IsDBNull(1);
                        if (hasPage && hasUrl)
                            RequestValidation.Throw("opensPageId", "opensPageId and forwardUrl cannot both be set");
                    }
                    var after = await QrRead.ByIdAsync(conn, tx, id, token)
                        ?? throw NotFound();
                    var stamp = await audit.RecordAsync(conn, tx, "update", "qr_code",
                        id.ToString(CultureInfo.InvariantCulture), before, after, token);
                    after.Audit = stamp;
                    return after;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminQrCodes")
            .Accepts<PatchQrCodeRequest>("application/json")
            .Produces<QrCodeDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/qr-codes/{id}/attach [snapshot]. Closes the open attachment
    // (to_at = now()), opens a new one, folds the last hour's unattached scans.
    private static void MapAttach(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/qr-codes/{id:long}/attach",
            async (long id, AttachQrCodeRequest body, HttpContext ctx,
                   AdminSnapshotTransaction snap, AuditRecorder audit, CancellationToken ct) =>
            {
                if (body.PlaceId <= 0) RequestValidation.Throw("placeId", "required");
                var email = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<QrCodeDto>(async (conn, tx, token) =>
                {
                    var before = await QrRead.ByIdAsync(conn, tx, id, token);
                    if (before is null) throw NotFound();
                    await using (var placeCheck = new NpgsqlCommand(
                        "select 1 from place where id = $1;", conn, tx))
                    {
                        placeCheck.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.PlaceId });
                        var r = await placeCheck.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull) throw NotFound();
                    }
                    // Close the open attachment for this code, if any.
                    await using (var close = new NpgsqlCommand(@"
update qr_attachment set to_at = now()
where qr_code_id = $1 and to_at is null;", conn, tx))
                    {
                        close.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await close.ExecuteNonQueryAsync(token);
                    }
                    // Open a new attachment.
                    long newAttachmentId;
                    await using (var open = new NpgsqlCommand(@"
insert into qr_attachment (qr_code_id, place_id, from_at, attached_by)
values ($1, $2, now(), $3) returning id;", conn, tx))
                    {
                        open.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        open.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.PlaceId });
                        open.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                        newAttachmentId = (long)(await open.ExecuteScalarAsync(token) ?? 0L);
                    }
                    // Fold unattached scans of this code from the last hour.
                    await using (var fold = new NpgsqlCommand(@"
update qr_scan set attachment_id = $1
where qr_code_id = $2 and attachment_id is null and at > now() - interval '1 hour';", conn, tx))
                    {
                        fold.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = newAttachmentId });
                        fold.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await fold.ExecuteNonQueryAsync(token);
                    }
                    var after = await QrRead.ByIdAsync(conn, tx, id, token)
                        ?? throw NotFound();
                    var stamp = await audit.RecordAsync(conn, tx, "attach", "qr_code",
                        id.ToString(CultureInfo.InvariantCulture), before, after, token);
                    after.Audit = stamp;
                    return after;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminQrCodes")
            .Accepts<AttachQrCodeRequest>("application/json")
            .Produces<QrCodeDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/qr-codes/{id}/detach [snapshot]. Idempotent: closes the open
    // attachment, else no-op.
    private static void MapDetach(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/qr-codes/{id:long}/detach",
            async (long id, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                var (dto, _) = await snap.RunAsync<QrCodeDto>(async (conn, tx, token) =>
                {
                    var before = await QrRead.ByIdAsync(conn, tx, id, token);
                    if (before is null) throw NotFound();
                    await using (var close = new NpgsqlCommand(@"
update qr_attachment set to_at = now()
where qr_code_id = $1 and to_at is null;", conn, tx))
                    {
                        close.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await close.ExecuteNonQueryAsync(token);
                    }
                    var after = await QrRead.ByIdAsync(conn, tx, id, token)
                        ?? throw NotFound();
                    var stamp = await audit.RecordAsync(conn, tx, "detach", "qr_code",
                        id.ToString(CultureInfo.InvariantCulture), before, after, token);
                    after.Audit = stamp;
                    return after;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminQrCodes")
            .Produces<QrCodeDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // DELETE /admin/qr-codes/{id} [snapshot], admin only.
    private static void MapDelete(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/qr-codes/{id:long}",
            async (long id, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await snap.RunAsync<object?>(async (conn, tx, token) =>
                {
                    var before = await QrRead.ByIdAsync(conn, tx, id, token);
                    if (before is null) throw NotFound();
                    await using var del = new NpgsqlCommand(
                        "delete from qr_code where id = $1;", conn, tx);
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var rows = await del.ExecuteNonQueryAsync(token);
                    if (rows == 0) throw NotFound();
                    await audit.RecordAsync(conn, tx, "delete", "qr_code",
                        id.ToString(CultureInfo.InvariantCulture), before, null, token);
                    return null;
                }, ct);
                return Results.NoContent();
            })
            .WithTags("AdminQrCodes")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // Public: POST /qr-codes/{tag}/scans. Always 204 (contracts 4.3, 4.5a).
    // Unknown or inactive tags are dropped silently.
    private static void MapScanBeacon(IEndpointRouteBuilder app)
    {
        app.MapPost("/qr-codes/{tag}/scans",
            async (string tag, HttpContext ctx, WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                // The body is optional and its fields are informational; a bad body
                // still answers 204 so the address reveals nothing.
                string? referrer = null;
                try
                {
                    if (ctx.Request.ContentLength is > 0)
                    {
                        var body = await JsonSerializer.DeserializeAsync<QrScanRequest>(
                            ctx.Request.Body, Wmsfo.Api.Objects.CanonicalJson.Options, ct);
                        referrer = body?.Referrer;
                    }
                }
                catch (JsonException) { }

                var userAgent = ctx.Request.Headers.UserAgent.ToString();
                var isBot = IsBotUserAgent(userAgent);
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                var ipHash = ComputeIpHash(options.ScanSalt, clientIp);

                await using var conn = new NpgsqlConnection(connections.App);
                try
                {
                    await conn.OpenAsync(ct);
                    long? codeId = null;
                    bool active = false;
                    await using (var lookup = new NpgsqlCommand(
                        "select id, active from qr_code where tag = $1;", conn))
                    {
                        lookup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = tag });
                        await using var reader = await lookup.ExecuteReaderAsync(ct);
                        if (await reader.ReadAsync(ct))
                        {
                            codeId = reader.GetInt64(0);
                            active = reader.GetBoolean(1);
                        }
                    }
                    if (codeId is not long cid || !active)
                    {
                        return Results.NoContent();
                    }
                    // Find open attachment (may be null).
                    long? attachmentId = null;
                    await using (var att = new NpgsqlCommand(
                        "select id from qr_attachment where qr_code_id = $1 and to_at is null;", conn))
                    {
                        att.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cid });
                        var r = await att.ExecuteScalarAsync(ct);
                        if (r is not null && r is not DBNull) attachmentId = Convert.ToInt64(r);
                    }
                    // Find current event id (nullable).
                    long? eventId = null;
                    await using (var evt = new NpgsqlCommand(
                        "select id from event where is_current;", conn))
                    {
                        var r = await evt.ExecuteScalarAsync(ct);
                        if (r is not null && r is not DBNull) eventId = Convert.ToInt64(r);
                    }
                    // Detect repeat: any scan on this code with the same ip_hash
                    // and the same user_agent within ten seconds.
                    bool isRepeat = false;
                    await using (var rep = new NpgsqlCommand(@"
select 1 from qr_scan
where qr_code_id = $1 and ip_hash = $2 and coalesce(user_agent, '') = $3
  and at > now() - interval '10 seconds'
limit 1;", conn))
                    {
                        rep.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cid });
                        rep.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Size = 64, Value = ipHash });
                        rep.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = userAgent ?? "" });
                        var r = await rep.ExecuteScalarAsync(ct);
                        if (r is not null && r is not DBNull) isRepeat = true;
                    }
                    await using var ins = new NpgsqlCommand(@"
insert into qr_scan (qr_code_id, attachment_id, event_id, user_agent, referrer, ip_hash, is_bot, is_repeat)
values ($1, $2, $3, $4, $5, $6, $7, $8);", conn);
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cid });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)attachmentId ?? DBNull.Value });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)eventId ?? DBNull.Value });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)userAgent ?? DBNull.Value });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)referrer ?? DBNull.Value });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Size = 64, Value = ipHash });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = isBot });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = isRepeat });
                    await ins.ExecuteNonQueryAsync(ct);
                }
                catch
                {
                    // Any error still answers 204 (the address reveals nothing).
                }
                return Results.NoContent();
            })
            .WithTags("Public")
            .Produces(StatusCodes.Status204NoContent)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireRateLimiting(RateLimitPolicies.ContactPerIp);
    }

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "qr code not found");

    private static bool IsHttpsUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, "https", StringComparison.Ordinal);
    }

    // Parse a PATCH body distinguishing "field absent" from "field null".
    private static PatchQrCodeRequest ParseQrPatch(JsonElement raw)
    {
        var patch = new PatchQrCodeRequest();
        if (raw.ValueKind != JsonValueKind.Object) return patch;
        foreach (var prop in raw.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "opensPageId":
                    patch.HasOpensPageId = true;
                    patch.OpensPageId = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetInt64();
                    break;
                case "forwardUrl":
                    patch.HasForwardUrl = true;
                    patch.ForwardUrl = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
                    break;
                case "note":
                    patch.Note = prop.Value.GetString();
                    break;
                case "active":
                    patch.Active = prop.Value.GetBoolean();
                    break;
                default:
                    RequestValidation.Throw(prop.Name, "unknown field");
                    break;
            }
        }
        return patch;
    }
}
