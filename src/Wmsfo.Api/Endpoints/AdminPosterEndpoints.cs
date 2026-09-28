using System.Globalization;
using System.Text;
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
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 Posters (Editor, the `events` capability). A poster is the
// admin panel's own document: a name, an optional flight recording, and the
// panel's layout, which the API stores opaquely and never reads. Nothing here
// is snapshot-affecting, so every write runs in a plain transaction.
public static class AdminPosterEndpoints
{
    // The largest layout accepted, in bytes of its canonical JSON.
    public const int LayoutMaxBytes = 32 * 1024;

    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapCreate(app);
        MapGet(app);
        MapPatch(app);
        MapDelete(app);
    }

    // GET /admin/posters → 200 { items: PosterSummary[] } newest first.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/posters",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<PosterSummaryDto>();
                await using var cmd = new NpgsqlCommand(
                    "select id, name, route_id, updated_at from poster order by id desc;", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new PosterSummaryDto
                    {
                        Id = reader.GetInt64(0),
                        Name = reader.GetString(1),
                        RouteId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(3),
                    });
                }
                return Results.Ok(new ItemsResponse<PosterSummaryDto> { Items = items });
            })
            .WithTags("AdminPosters")
            .Produces<ItemsResponse<PosterSummaryDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireCapability(ApiKeyCapabilities.Events)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/posters → 201 Poster. name 1 to 200; routeId a route id or
    // null (404 unknown route); layout a JSON object of at most 32 KB
    // canonical or null.
    private static void MapCreate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/posters",
            async (CreatePosterRequest body, HttpContext ctx, AdminSnapshotTransaction snap,
                   AuditRecorder audit, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 200)
                    v.Field("name", "must be 1 to 200 characters");
                var route = ReadRouteId(body.RouteId, v);
                var layout = ReadLayout(body.Layout, v);
                v.ThrowIfInvalid();
                var email = AdminHelpers.RequireAdminEmail(ctx);

                var dto = await snap.RunWithoutSnapshotAsync<PosterDto>(async (conn, tx, token) =>
                {
                    if (route.Value is long routeId) await RequireRouteAsync(conn, tx, routeId, token);
                    long newId;
                    await using (var insert = new NpgsqlCommand(@"
insert into poster (name, route_id, layout, created_by, updated_at)
values ($1, $2, $3, $4, now())
returning id;", conn, tx))
                    {
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Name.Trim() });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)route.Value ?? DBNull.Value });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)layout.Value ?? DBNull.Value });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                        newId = (long)(await insert.ExecuteScalarAsync(token) ?? 0L);
                    }
                    var created = await ReadByIdAsync(conn, tx, newId, token) ?? throw NotFound();
                    created.Audit = await audit.RecordAsync(conn, tx, "create", "poster",
                        newId.ToString(CultureInfo.InvariantCulture),
                        before: null, after: created, token);
                    return created;
                }, ct);
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminPosters")
            .Accepts<CreatePosterRequest>("application/json")
            .Produces<PosterDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireCapability(ApiKeyCapabilities.Events)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // GET /admin/posters/{id} → 200 Poster, layout included.
    private static void MapGet(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/posters/{id:long}",
            async (long id, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var dto = await ReadByIdAsync(conn, null, id, ct) ?? throw NotFound();
                return Results.Ok(dto);
            })
            .WithTags("AdminPosters")
            .Produces<PosterDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireCapability(ApiKeyCapabilities.Events)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PATCH /admin/posters/{id} → 200 Poster. Any of name, routeId (a route id
    // sets it, null clears it, 404 unknown route), layout (a JSON object of at
    // most 32 KB canonical sets it, null clears it); an absent field is left
    // unchanged.
    private static void MapPatch(IEndpointRouteBuilder app)
    {
        app.MapPatch("/admin/posters/{id:long}",
            async (long id, PatchPosterRequest body, HttpContext ctx, AdminSnapshotTransaction snap,
                   AuditRecorder audit, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (body.Name is not null && (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 200))
                    v.Field("name", "must be 1 to 200 characters");
                var route = ReadRouteId(body.RouteId, v);
                var layout = ReadLayout(body.Layout, v);
                v.ThrowIfInvalid();
                _ = AdminHelpers.RequireAdminEmail(ctx);

                var dto = await snap.RunWithoutSnapshotAsync<PosterDto>(async (conn, tx, token) =>
                {
                    await using (var lockRow = new NpgsqlCommand(
                        "select 1 from poster where id = $1 for update;", conn, tx))
                    {
                        lockRow.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var found = await lockRow.ExecuteScalarAsync(token);
                        if (found is null || found is DBNull) throw NotFound();
                    }
                    var before = await ReadByIdAsync(conn, tx, id, token) ?? throw NotFound();
                    if (route.Value is long routeId) await RequireRouteAsync(conn, tx, routeId, token);

                    var sets = new List<string>();
                    var parameters = new List<NpgsqlParameter>();
                    void Set(string column, NpgsqlDbType type, object? value)
                    {
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value });
                        sets.Add($"{column} = ${parameters.Count}");
                    }
                    if (body.Name is not null) Set("name", NpgsqlDbType.Text, body.Name.Trim());
                    if (route.Set) Set("route_id", NpgsqlDbType.Bigint, route.Value);
                    if (layout.Set) Set("layout", NpgsqlDbType.Jsonb, layout.Value);
                    sets.Add("updated_at = now()");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await using (var update = new NpgsqlCommand(
                        $"update poster set {string.Join(", ", sets)} where id = ${parameters.Count};", conn, tx))
                    {
                        foreach (var p in parameters) update.Parameters.Add(p);
                        await update.ExecuteNonQueryAsync(token);
                    }

                    var updated = await ReadByIdAsync(conn, tx, id, token) ?? throw NotFound();
                    updated.Audit = await audit.RecordAsync(conn, tx, "update", "poster",
                        id.ToString(CultureInfo.InvariantCulture),
                        before, updated, token);
                    return updated;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminPosters")
            .Accepts<PatchPosterRequest>("application/json")
            .Produces<PosterDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireCapability(ApiKeyCapabilities.Events)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // DELETE /admin/posters/{id} → 204. Preview, apply (nothing depends on a
    // poster), delete, and the audit row with before = { ...poster, impact }
    // (api.md 5b).
    private static void MapDelete(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/posters/{id:long}",
            async (long id, HttpContext ctx, AdminSnapshotTransaction snap, AuditRecorder audit, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await snap.RunWithoutSnapshotAsync<object?>(async (conn, tx, token) =>
                {
                    await using (var lockRow = new NpgsqlCommand(
                        "select 1 from poster where id = $1 for update;", conn, tx))
                    {
                        lockRow.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var found = await lockRow.ExecuteScalarAsync(token);
                        if (found is null || found is DBNull) throw NotFound();
                    }
                    var before = await ReadByIdAsync(conn, tx, id, token) ?? throw NotFound();
                    var impact = await Impact.PosterImpactQueries.PreviewAsync(conn, tx, id, token);
                    await Impact.PosterImpactQueries.ApplyAsync(conn, tx, id, token);
                    await using (var del = new NpgsqlCommand("delete from poster where id = $1;", conn, tx))
                    {
                        del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await del.ExecuteNonQueryAsync(token);
                    }
                    await audit.RecordAsync(conn, tx, "delete", "poster",
                        id.ToString(CultureInfo.InvariantCulture),
                        before: Impact.ImpactBefore.Combine(before, impact), after: null, token);
                    return null;
                }, ct);
                return Results.NoContent();
            })
            .WithTags("AdminPosters")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireCapability(ApiKeyCapabilities.Events)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- shared plumbing ---

    // routeId: a positive integer sets it, null clears it, absent leaves it
    // unchanged; anything else is a validation failure on routeId.
    private static (bool Set, long? Value) ReadRouteId(JsonElement el, RequestValidation v)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Undefined:
                return (false, null);
            case JsonValueKind.Null:
                return (true, null);
            case JsonValueKind.Number when el.TryGetInt64(out var id) && id > 0:
                return (true, id);
            default:
                v.Field("routeId", "must be a route id or null");
                return (false, null);
        }
    }

    // layout: a JSON object whose canonical form is at most LayoutMaxBytes
    // sets it (the canonical text is what is stored), null clears it, absent
    // leaves it unchanged; anything else is a validation failure on layout.
    private static (bool Set, string? Value) ReadLayout(JsonElement el, RequestValidation v)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Undefined:
                return (false, null);
            case JsonValueKind.Null:
                return (true, null);
            case JsonValueKind.Object:
                var canonical = CanonicalJson.SerializeOpaqueToUtf8Bytes(el);
                if (canonical.Length > LayoutMaxBytes)
                {
                    v.Field("layout", "must be at most 32768 bytes as canonical JSON");
                    return (false, null);
                }
                return (true, Encoding.UTF8.GetString(canonical));
            default:
                v.Field("layout", "must be a JSON object or null");
                return (false, null);
        }
    }

    private static async Task RequireRouteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long routeId, CancellationToken ct)
    {
        await using var check = new NpgsqlCommand("select 1 from route where id = $1;", conn, tx);
        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = routeId });
        var r = await check.ExecuteScalarAsync(ct);
        if (r is null || r is DBNull)
            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "route not found");
    }

    private const string PosterSelectSql = @"
select p.id, p.name, p.route_id, p.layout::text, p.created_by, p.created_at, p.updated_at,
       a.action, a.actor, a.at
from poster p
left join lateral (
  select action, actor, at from audit_log
  where entity = 'poster' and entity_id = p.id::text
  order by id desc limit 1
) a on true
where p.id = $1;";

    private static async Task<PosterDto?> ReadByIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(PosterSelectSql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var dto = new PosterDto
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            RouteId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            CreatedBy = reader.GetString(4),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(6),
        };
        if (!reader.IsDBNull(3))
        {
            // jsonb reorders keys by length; the answer is the canonical form.
            using var stored = JsonDocument.Parse(reader.GetString(3));
            using var canonical = JsonDocument.Parse(CanonicalJson.SerializeOpaqueToUtf8Bytes(stored.RootElement));
            dto.Layout = canonical.RootElement.Clone();
        }
        if (!reader.IsDBNull(7))
        {
            dto.Audit = new AuditStampDto
            {
                Action = reader.GetString(7),
                By = reader.GetString(8),
                At = reader.GetFieldValue<DateTimeOffset>(9),
            };
        }
        return dto;
    }

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "poster not found");
}
