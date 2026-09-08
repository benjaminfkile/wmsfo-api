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
using Wmsfo.Api.Icons;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 Cookie Types (Admin). CRUD with an Icon value that is either
// a library id (checked against IconLibrary) or a ready svg media_asset id.
// Every write is [snapshot] and every write is refused with 409 event_live
// while any event carries status_id = 3 (sql.md 3.14 comment).
public static class AdminCookieTypeEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapCreate(app);
        MapPatch(app);
    }

    // GET /admin/cookie-types → 200 { items: CookieType[] } sorted by sort, id.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/cookie-types",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<CookieTypeDto>();
                await using var cmd = new NpgsqlCommand(@"
select id, name, icon, sort, active, created_at, updated_at
from cookie_type
order by sort, id;", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) items.Add(ReadRow(reader));
                return Results.Ok(new ItemsResponse<CookieTypeDto> { Items = items });
            })
            .WithTags("AdminCookieTypes")
            .Produces<ItemsResponse<CookieTypeDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/cookie-types [snapshot]. name 1..100, sort -1000..1000,
    // active required, icon nullable but validated when present. 409 event_live
    // while any event is at status 3.
    private static void MapCreate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/cookie-types",
            async (CreateCookieTypeRequest body, HttpContext ctx, AdminSnapshotTransaction snap,
                   IconLibrary icons, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 100)
                    v.Field("name", "must be 1 to 100 characters");
                if (body.Sort < -1000 || body.Sort > 1000)
                    v.Field("sort", "must be between -1000 and 1000");
                ValidateIconShape(body.Icon, v);
                v.ThrowIfInvalid();
                _ = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<CookieTypeDto>(async (conn, tx, token) =>
                {
                    await GuardNoLiveEventAsync(conn, tx, token);
                    await CheckIconAsync(conn, tx, body.Icon, icons, token);
                    long newId;
                    await using (var insert = new NpgsqlCommand(@"
insert into cookie_type (name, sort, active, icon, updated_at)
values ($1, $2, $3, $4::jsonb, now()) returning id;", conn, tx))
                    {
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Name.Trim() });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = body.Sort });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = body.Active });
                        insert.Parameters.Add(new NpgsqlParameter
                        {
                            NpgsqlDbType = NpgsqlDbType.Jsonb,
                            Value = (object?)IconToJson(body.Icon) ?? DBNull.Value,
                        });
                        newId = (long)(await insert.ExecuteScalarAsync(token) ?? 0L);
                    }
                    return await ReadByIdAsync(conn, tx, newId, token)
                        ?? throw NotFound("cookie type not found");
                }, ct);
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminCookieTypes")
            .Accepts<CreateCookieTypeRequest>("application/json")
            .Produces<CookieTypeDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PATCH /admin/cookie-types/{id} [snapshot]. Any subset of name/sort/active/icon.
    private static void MapPatch(IEndpointRouteBuilder app)
    {
        app.MapPatch("/admin/cookie-types/{id:long}",
            async (long id, PatchCookieTypeRequest body, HttpContext ctx, AdminSnapshotTransaction snap,
                   IconLibrary icons, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (body.Name is not null && (body.Name.Length < 1 || body.Name.Length > 100))
                    v.Field("name", "must be 1 to 100 characters");
                if (body.Sort is int sort && (sort < -1000 || sort > 1000))
                    v.Field("sort", "must be between -1000 and 1000");
                ValidateIconShape(body.Icon, v);
                v.ThrowIfInvalid();
                _ = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<CookieTypeDto>(async (conn, tx, token) =>
                {
                    await GuardNoLiveEventAsync(conn, tx, token);
                    await using (var check = new NpgsqlCommand(
                        "select 1 from cookie_type where id = $1 for update;", conn, tx))
                    {
                        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r = await check.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull) throw NotFound("cookie type not found");
                    }
                    if (body.Icon is not null) await CheckIconAsync(conn, tx, body.Icon, icons, token);

                    var sets = new List<string>();
                    var parameters = new List<NpgsqlParameter>();
                    var next = 1;
                    if (body.Name is not null) { sets.Add($"name = ${next++}"); parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Name.Trim() }); }
                    if (body.Sort is int s) { sets.Add($"sort = ${next++}"); parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = s }); }
                    if (body.Active is bool a) { sets.Add($"active = ${next++}"); parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = a }); }
                    if (body.Icon is not null)
                    {
                        sets.Add($"icon = ${next++}::jsonb");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = IconToJson(body.Icon)! });
                    }
                    if (sets.Count == 0)
                    {
                        return await ReadByIdAsync(conn, tx, id, token)
                            ?? throw NotFound("cookie type not found");
                    }
                    sets.Add("updated_at = now()");
                    var whereIdx = next;
                    var sql = $"update cookie_type set {string.Join(", ", sets)} where id = ${whereIdx};";
                    await using (var upd = new NpgsqlCommand(sql, conn, tx))
                    {
                        foreach (var p in parameters) upd.Parameters.Add(p);
                        upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await upd.ExecuteNonQueryAsync(token);
                    }
                    return await ReadByIdAsync(conn, tx, id, token)
                        ?? throw NotFound("cookie type not found");
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminCookieTypes")
            .Accepts<PatchCookieTypeRequest>("application/json")
            .Produces<CookieTypeDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- helpers ---

    private static ApiException NotFound(string message) =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, message);

    // sql.md 3.14 comment: cookie_type writes are locked (409 event_live) while
    // any event has status 3. Read once inside the transaction so a concurrent
    // status change is serialized against the snapshot row lock we already hold.
    private static async Task GuardNoLiveEventAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select 1 from event where status_id = 3 limit 1;", conn, tx);
        var r = await cmd.ExecuteScalarAsync(ct);
        if (r is not null && r is not DBNull)
            throw new ApiException(StatusCodes.Status409Conflict, "event_live", "an event is live");
    }

    // Icon primitive shape: source is "library" or "media", id is required.
    private static void ValidateIconShape(IconValue? icon, RequestValidation v)
    {
        if (icon is null) return;
        if (icon.Source != "library" && icon.Source != "media")
            v.Field("icon.source", "must be library or media");
        if (string.IsNullOrEmpty(icon.Id))
            v.Field("icon.id", "must be a non-empty string");
    }

    // Library id must exist in the compiled library; media id must be a ready svg.
    private static async Task CheckIconAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, IconValue? icon, IconLibrary library, CancellationToken ct)
    {
        if (icon is null) return;
        if (icon.Source == "library")
        {
            if (!library.Contains(icon.Id))
                throw new ApiException(StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed, "icon.id is not a known library id");
            return;
        }
        if (icon.Source == "media")
        {
            if (!Guid.TryParse(icon.Id, out var mediaId))
                throw new ApiException(StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed, "icon.id must be a uuid for media source");
            string? kind = null;
            string? state = null;
            await using (var cmd = new NpgsqlCommand(
                "select kind, state from media_asset where id = $1;", conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    throw NotFound("media not found");
                kind = reader.GetString(0);
                state = reader.GetString(1);
            }
            if (!string.Equals(state, "ready", StringComparison.Ordinal))
                throw new ApiException(StatusCodes.Status409Conflict,
                    "media_not_ready", "media asset is not ready");
            if (!string.Equals(kind, "svg", StringComparison.Ordinal))
                throw new ApiException(StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed, "media icon must be an svg asset");
        }
    }

    private static string? IconToJson(IconValue? icon) =>
        icon is null ? null : $"{{\"source\":\"{icon.Source}\",\"id\":\"{icon.Id}\"}}";

    private static async Task<CookieTypeDto?> ReadByIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select id, name, icon, sort, active, created_at, updated_at
from cookie_type where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadRow(reader);
    }

    private static CookieTypeDto ReadRow(NpgsqlDataReader reader)
    {
        IconValue? icon = null;
        if (!reader.IsDBNull(2))
        {
            var json = reader.GetString(2);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var source = doc.RootElement.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
                var id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(id))
                    icon = new IconValue { Source = source, Id = id };
            }
        }
        return new CookieTypeDto
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Icon = icon,
            Sort = reader.GetInt32(3),
            Active = reader.GetBoolean(4),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(6),
        };
    }
}
