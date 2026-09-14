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

// contracts 4.5a Places (Canvasser). Every write is [snapshot] except location
// PUT/DELETE (pins never reach the site). Delete is Admin-only and rejects
// 409 place_has_children (any child) and 409 place_has_codes (any open attach).
public static class PlaceEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapCreate(app);
        MapPatch(app);
        MapPutLocation(app);
        MapDeleteLocation(app);
        MapDelete(app);
        MapMap(app);
    }

    // GET /admin/places → 200 { items } in tree order.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/places",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = await PlaceRead.TreeAsync(conn, null, ct);
                return Results.Ok(new ItemsResponse<PlaceDto> { Items = items });
            })
            .WithTags("AdminPlaces")
            .Produces<ItemsResponse<PlaceDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/places [snapshot]. name 1..120 unique among siblings,
    // description 0..500. 404 when parentId names nothing.
    private static void MapCreate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/places",
            async (CreatePlaceRequest body, HttpContext ctx, AdminSnapshotTransaction snap,
                   AuditRecorder audit, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                var name = (body.Name ?? "").Trim();
                if (name.Length < 1 || name.Length > 120)
                    v.Field("name", "must be 1 to 120 characters");
                var description = body.Description ?? "";
                if (description.Length > 500)
                    v.Field("description", "must be 0 to 500 characters");
                if (body.OpensPageId is not null && !string.IsNullOrEmpty(body.ForwardUrl))
                    v.Field("opensPageId", "opensPageId and forwardUrl cannot both be set");
                if (!string.IsNullOrEmpty(body.ForwardUrl) && !IsHttpsUrl(body.ForwardUrl))
                    v.Field("forwardUrl", "must be an https URL");
                v.ThrowIfInvalid();
                var email = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<PlaceDto>(async (conn, tx, token) =>
                {
                    if (body.ParentId is long pid)
                    {
                        await using var check = new NpgsqlCommand("select 1 from place where id = $1;", conn, tx);
                        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pid });
                        var r = await check.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull)
                            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "parent not found");
                    }
                    if (body.OpensPageId is long pageId)
                    {
                        await using var check = new NpgsqlCommand("select 1 from page where id = $1;", conn, tx);
                        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pageId });
                        var r = await check.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull)
                            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "page not found");
                    }
                    long newId;
                    try
                    {
                        await using var ins = new NpgsqlCommand(@"
insert into place (parent_id, name, description, opens_page_id, forward_url, created_by, updated_at)
values ($1, $2, $3, $4, $5, $6, now()) returning id;", conn, tx);
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)body.ParentId ?? DBNull.Value });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = description });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)body.OpensPageId ?? DBNull.Value });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)body.ForwardUrl ?? DBNull.Value });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                        newId = (long)(await ins.ExecuteScalarAsync(token) ?? 0L);
                    }
                    catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == "place_sibling_name")
                    {
                        throw new ApiException(StatusCodes.Status409Conflict, "place_name_taken", "name taken among siblings");
                    }
                    var after = await PlaceRead.ByIdAsync(conn, tx, newId, token)
                        ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "place not found");
                    var stamp = await audit.RecordAsync(conn, tx, "create", "place",
                        newId.ToString(CultureInfo.InvariantCulture), before: null, after: after, token);
                    after.Audit = stamp;
                    return after;
                }, ct);
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminPlaces")
            .Accepts<CreatePlaceRequest>("application/json")
            .Produces<PlaceDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PATCH /admin/places/{id} [snapshot]. Any of parentId, name, description,
    // opensPageId, forwardUrl. 400 place_cycle if moving under itself/descendant.
    // 409 place_name_taken if the name is not unique among siblings.
    private static void MapPatch(IEndpointRouteBuilder app)
    {
        app.MapPatch("/admin/places/{id:long}",
            async (long id, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit, CancellationToken ct) =>
            {
                var raw = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body,
                    Wmsfo.Api.Objects.CanonicalJson.Options, ct);
                var patch = ParsePlacePatch(raw);
                _ = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<PlaceDto>(async (conn, tx, token) =>
                {
                    var before = await PlaceRead.ByIdAsync(conn, tx, id, token);
                    if (before is null) throw NotFound();

                    // Cycle rule: refuse a move under the place itself or one of its
                    // descendants. Walk up from the new parent.
                    if (patch.HasParentId && patch.ParentId is long newParent)
                    {
                        if (newParent == id)
                            throw new ApiException(StatusCodes.Status400BadRequest, "place_cycle", "a place cannot move under itself");
                        await using var walk = new NpgsqlCommand(@"
with recursive chain(id, parent_id, depth) as (
  select id, parent_id, 0 from place where id = $1
  union all
  select p.id, p.parent_id, c.depth + 1 from place p join chain c on p.id = c.parent_id where c.depth < 32
)
select 1 from chain where id = $2;", conn, tx);
                        walk.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = newParent });
                        walk.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r = await walk.ExecuteScalarAsync(token);
                        if (r is not null && r is not DBNull)
                            throw new ApiException(StatusCodes.Status400BadRequest, "place_cycle", "a place cannot move under its descendant");
                    }
                    if (patch.HasOpensPageId && patch.OpensPageId is not null && patch.HasForwardUrl && !string.IsNullOrEmpty(patch.ForwardUrl))
                        RequestValidation.Throw("opensPageId", "opensPageId and forwardUrl cannot both be set");

                    var sets = new List<string>();
                    var parameters = new List<NpgsqlParameter>();
                    var next = 1;
                    if (patch.HasParentId)
                    {
                        if (patch.ParentId is null) sets.Add("parent_id = null");
                        else { sets.Add($"parent_id = ${next++}"); parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = patch.ParentId.Value }); }
                    }
                    if (patch.Name is not null)
                    {
                        var trimmed = patch.Name.Trim();
                        if (trimmed.Length < 1 || trimmed.Length > 120)
                            RequestValidation.Throw("name", "must be 1 to 120 characters");
                        sets.Add($"name = ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = trimmed });
                    }
                    if (patch.Description is not null)
                    {
                        if (patch.Description.Length > 500)
                            RequestValidation.Throw("description", "must be 0 to 500 characters");
                        sets.Add($"description = ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = patch.Description });
                    }
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
                    if (sets.Count > 0)
                    {
                        sets.Add("updated_at = now()");
                        var whereIdx = next;
                        try
                        {
                            await using var upd = new NpgsqlCommand(
                                $"update place set {string.Join(", ", sets)} where id = ${whereIdx};", conn, tx);
                            foreach (var p in parameters) upd.Parameters.Add(p);
                            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                            await upd.ExecuteNonQueryAsync(token);
                        }
                        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == "place_sibling_name")
                        {
                            throw new ApiException(StatusCodes.Status409Conflict, "place_name_taken", "name taken among siblings");
                        }
                    }
                    var after = await PlaceRead.ByIdAsync(conn, tx, id, token)
                        ?? throw NotFound();
                    var stamp = await audit.RecordAsync(conn, tx, "update", "place",
                        id.ToString(CultureInfo.InvariantCulture), before, after, token);
                    after.Audit = stamp;
                    return after;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminPlaces")
            .Accepts<PatchPlaceRequest>("application/json")
            .Produces<PlaceDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PUT /admin/places/{id}/location. Not snapshot-affecting. Source in
    // (phone, search, drag); accuracy_m null unless phone. Audit action: location.
    private static void MapPutLocation(IEndpointRouteBuilder app)
    {
        app.MapPut("/admin/places/{id:long}/location",
            async (long id, PutPlaceLocationRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, AuditRecorder audit, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (body.Lat < -90 || body.Lat > 90) v.Field("lat", "must be between -90 and 90");
                if (body.Lng < -180 || body.Lng > 180) v.Field("lng", "must be between -180 and 180");
                if (body.Source is not ("phone" or "search" or "drag"))
                    v.Field("source", "must be phone, search, or drag");
                if (body.Source != "phone" && body.AccuracyM is not null)
                    v.Field("accuracyM", "must be null unless source is phone");
                v.ThrowIfInvalid();
                var email = AdminHelpers.RequireAdminEmail(ctx);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                var before = await PlaceRead.ByIdAsync(conn, tx, id, ct);
                if (before is null) throw NotFound();
                await using (var upd = new NpgsqlCommand(@"
update place set lat = $1, lng = $2, accuracy_m = $3, pin_source = $4, pinned_by = $5, pinned_at = now(), updated_at = now()
where id = $6;", conn, tx))
                {
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = body.Lat });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = body.Lng });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)body.AccuracyM ?? DBNull.Value });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Source });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await upd.ExecuteNonQueryAsync(ct);
                }
                var after = await PlaceRead.ByIdAsync(conn, tx, id, ct) ?? throw NotFound();
                var stamp = await audit.RecordAsync(conn, tx, "location", "place",
                    id.ToString(CultureInfo.InvariantCulture), before, after, ct);
                after.Audit = stamp;
                await tx.CommitAsync(ct);
                return Results.Ok(after);
            })
            .WithTags("AdminPlaces")
            .Accepts<PutPlaceLocationRequest>("application/json")
            .Produces<PlaceDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapDeleteLocation(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/places/{id:long}/location",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, AuditRecorder audit, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                var before = await PlaceRead.ByIdAsync(conn, tx, id, ct);
                if (before is null) throw NotFound();
                await using (var upd = new NpgsqlCommand(@"
update place set lat = null, lng = null, accuracy_m = null, pin_source = null, pinned_by = null, pinned_at = null, updated_at = now()
where id = $1;", conn, tx))
                {
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await upd.ExecuteNonQueryAsync(ct);
                }
                var after = await PlaceRead.ByIdAsync(conn, tx, id, ct) ?? throw NotFound();
                var stamp = await audit.RecordAsync(conn, tx, "location", "place",
                    id.ToString(CultureInfo.InvariantCulture), before, after, ct);
                after.Audit = stamp;
                await tx.CommitAsync(ct);
                return Results.Ok(after);
            })
            .WithTags("AdminPlaces")
            .Produces<PlaceDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // DELETE /admin/places/{id} [snapshot], admin only. Deletes the place and
    // everything under it (place.parent_id cascades). Open attachments in the
    // subtree close first, so their codes become unattached; every stay keeps
    // its history row with place_id null (qr_attachment.place_id sets null).
    private static void MapDelete(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/places/{id:long}",
            async (long id, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await snap.RunAsync<object?>(async (conn, tx, token) =>
                {
                    var before = await PlaceRead.ByIdAsync(conn, tx, id, token);
                    if (before is null) throw NotFound();
                    await using (var close = new NpgsqlCommand(@"
with recursive subtree(id) as (
  select id from place where id = $1
  union all
  select p.id from place p join subtree s on p.parent_id = s.id
)
update qr_attachment set to_at = now()
where to_at is null and place_id in (select id from subtree);", conn, tx))
                    {
                        close.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await close.ExecuteNonQueryAsync(token);
                    }
                    await using var del = new NpgsqlCommand(
                        "delete from place where id = $1;", conn, tx);
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await del.ExecuteNonQueryAsync(token);
                    await audit.RecordAsync(conn, tx, "delete", "place",
                        id.ToString(CultureInfo.InvariantCulture), before, null, token);
                    return null;
                }, ct);
                return Results.NoContent();
            })
            .WithTags("AdminPlaces")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // GET /admin/places/map?eventId=&from=&to=. Returns pinned places with
    // people counts under them, plus `unpinned` and `unattached` totals.
    private static void MapMap(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/places/map",
            async (HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                long? eventId = null;
                DateTimeOffset? from = null;
                DateTimeOffset? to = null;
                var v = new RequestValidation();
                if (ctx.Request.Query.TryGetValue("eventId", out var eventIdRaw) && !string.IsNullOrEmpty(eventIdRaw))
                {
                    if (long.TryParse(eventIdRaw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e))
                        eventId = e;
                    else v.Field("eventId", "must be a bigint");
                }
                if (ctx.Request.Query.TryGetValue("from", out var fromRaw) && !string.IsNullOrEmpty(fromRaw))
                {
                    if (DateTimeOffset.TryParse(fromRaw.ToString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var f))
                        from = f;
                    else v.Field("from", "must be rfc3339");
                }
                if (ctx.Request.Query.TryGetValue("to", out var toRaw) && !string.IsNullOrEmpty(toRaw))
                {
                    if (DateTimeOffset.TryParse(toRaw.ToString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tt))
                        to = tt;
                    else v.Field("to", "must be rfc3339");
                }
                v.ThrowIfInvalid();

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                var response = new PlacePinsResponse();
                // Read all places (id -> parent, name, pin fields).
                var byId = new Dictionary<long, (long? Parent, string Name, double? Lat, double? Lng)>();
                await using (var cmd = new NpgsqlCommand(
                    "select id, parent_id, name, lat, lng from place;", conn))
                {
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        byId[reader.GetInt64(0)] = (
                            reader.IsDBNull(1) ? null : reader.GetInt64(1),
                            reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetDouble(3),
                            reader.IsDBNull(4) ? null : reader.GetDouble(4));
                    }
                }
                // For each place with a pin, count unflagged scans in its subtree
                // whose place matches the filters.
                var scansByAttachmentPlace = new Dictionary<long, int>();
                var codesInPlace = new Dictionary<long, List<(long CodeId, string Tag, int People, string PlaceName)>>();
                int unattachedScans = 0;
                {
                    var conditions = new List<string> { "s.is_bot = false", "s.is_repeat = false" };
                    var parameters = new List<NpgsqlParameter>();
                    var next = 1;
                    if (eventId is long eid)
                    {
                        conditions.Add($"s.event_id = ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eid });
                    }
                    if (from is DateTimeOffset f)
                    {
                        conditions.Add($"s.at >= ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = f.ToUniversalTime() });
                    }
                    if (to is DateTimeOffset tt)
                    {
                        conditions.Add($"s.at < ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = tt.ToUniversalTime() });
                    }
                    var where = string.Join(" and ", conditions);
                    await using var cmd = new NpgsqlCommand($@"
select
  a.place_id, c.id, c.tag,
  count(*) filter (where s.attachment_id is not null)::int as people_attached,
  count(*) filter (where s.attachment_id is null)::int as people_unattached
from qr_scan s
left join qr_attachment a on a.id = s.attachment_id
left join qr_code c on c.id = s.qr_code_id
where {where}
group by a.place_id, c.id, c.tag;", conn);
                    foreach (var p in parameters) cmd.Parameters.Add(p);
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var placeId = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
                        var codeId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                        var tag = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        var peopleAttached = reader.GetInt32(3);
                        var peopleUnattached = reader.GetInt32(4);
                        if (placeId is long pid)
                        {
                            scansByAttachmentPlace.TryGetValue(pid, out var running);
                            scansByAttachmentPlace[pid] = running + peopleAttached;
                            if (!codesInPlace.TryGetValue(pid, out var list))
                            {
                                list = new List<(long, string, int, string)>();
                                codesInPlace[pid] = list;
                            }
                            var placeName = byId.TryGetValue(pid, out var pl) ? pl.Name : "";
                            list.Add((codeId ?? 0, tag, peopleAttached, placeName));
                        }
                        if (peopleUnattached > 0) unattachedScans += peopleUnattached;
                    }
                }

                // Every pinned place: the count from itself + all descendants that
                // resolve their pin to this place.
                bool ResolvesPinTo(long placeId, long targetPin)
                {
                    // Walk up ancestor chain until a pin is set; when it is, the
                    // pin resolves to that ancestor.
                    var cur = placeId;
                    var depth = 0;
                    while (depth < 33 && byId.TryGetValue(cur, out var row))
                    {
                        if (row.Lat is not null) return cur == targetPin;
                        if (row.Parent is null) return false;
                        cur = row.Parent.Value;
                        depth++;
                    }
                    return false;
                }
                // Collect subtree children for every pinned place.
                var pinnedIds = byId.Where(kv => kv.Value.Lat is not null).Select(kv => kv.Key).ToList();
                foreach (var pinId in pinnedIds.OrderBy(x => x))
                {
                    var pinRow = byId[pinId];
                    var pin = new PlacePinDto
                    {
                        PlaceId = pinId,
                        Name = pinRow.Name,
                        Path = await QrRead.PlacePathAsync(conn, null, pinId, ct),
                        Lat = pinRow.Lat!.Value,
                        Lng = pinRow.Lng!.Value,
                        People = 0,
                        Codes = new List<PlacePinCodeRefDto>(),
                    };
                    foreach (var (placeId, counts) in scansByAttachmentPlace)
                    {
                        if (ResolvesPinTo(placeId, pinId))
                        {
                            pin.People += counts;
                            if (codesInPlace.TryGetValue(placeId, out var codes))
                            {
                                foreach (var c in codes)
                                {
                                    pin.Codes.Add(new PlacePinCodeRefDto
                                    {
                                        Tag = c.Tag,
                                        PlaceName = c.PlaceName,
                                        People = c.People,
                                    });
                                }
                            }
                        }
                    }
                    response.Items.Add(pin);
                }
                // Unpinned: places with scans whose subtree has no resolved pin.
                foreach (var (placeId, count) in scansByAttachmentPlace)
                {
                    var pinnedTo = -1L;
                    var cur = placeId;
                    var depth = 0;
                    while (depth < 33 && byId.TryGetValue(cur, out var row))
                    {
                        if (row.Lat is not null) { pinnedTo = cur; break; }
                        if (row.Parent is null) break;
                        cur = row.Parent.Value;
                        depth++;
                    }
                    if (pinnedTo < 0) response.Unpinned += count;
                }
                response.Unattached = unattachedScans;
                return Results.Ok(response);
            })
            .WithTags("AdminPlaces")
            .Produces<PlacePinsResponse>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Canvasser)
            .RequireCapability(ApiKeyCapabilities.Qr)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "place not found");

    private static bool IsHttpsUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, "https", StringComparison.Ordinal);
    }

    private static PatchPlaceRequest ParsePlacePatch(JsonElement raw)
    {
        var patch = new PatchPlaceRequest();
        if (raw.ValueKind != JsonValueKind.Object) return patch;
        foreach (var prop in raw.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "parentId":
                    patch.HasParentId = true;
                    patch.ParentId = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetInt64();
                    break;
                case "name":
                    patch.Name = prop.Value.GetString();
                    break;
                case "description":
                    patch.Description = prop.Value.GetString();
                    break;
                case "opensPageId":
                    patch.HasOpensPageId = true;
                    patch.OpensPageId = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetInt64();
                    break;
                case "forwardUrl":
                    patch.HasForwardUrl = true;
                    patch.ForwardUrl = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
                    break;
                default:
                    RequestValidation.Throw(prop.Name, "unknown field");
                    break;
            }
        }
        return patch;
    }
}
