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

// api.md 11.1 Routes endpoints. The upload path is: canonicalize the body,
// hash it, look up route by s3_key first (hit: 200 that row and PUT nothing),
// PUT the object with the immutable header (one attempt, 3 s, else 502
// route_write_failed), insert the row. A 23505 on route_s3_key_key from a
// concurrent identical upload re-reads and answers 200.
public static class AdminRouteEndpoints
{
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    public const string JsonContentType = "application/json; charset=utf-8";
    public static readonly TimeSpan PutTimeout = TimeSpan.FromSeconds(3);

    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapGet(app);
        MapCreate(app);
        MapFromEvent(app);
        MapDelete(app);
    }

    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/routes",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<RouteDto>();
                await using var cmd = new NpgsqlCommand(@"
select id, name, url, s3_key, sha256, point_count, uploaded_by, created_at
from route
order by id desc;", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) items.Add(ReadRoute(reader));
                return Results.Ok(new ItemsResponse<RouteDto> { Items = items });
            })
            .WithTags("AdminRoutes")
            .Produces<ItemsResponse<RouteDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapGet(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/routes/{id:long}",
            async (long id, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(@"
select id, name, url, s3_key, sha256, point_count, uploaded_by, created_at
from route where id = $1;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "route not found");
                return Results.Ok(ReadRoute(reader));
            })
            .WithTags("AdminRoutes")
            .Produces<RouteDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/routes - upload. 5 MB body limit (checked first); 400 for
    // fewer than 2 or more than 50,000 points; 502 route_write_failed on PUT;
    // 200 with the existing row when the s3_key already exists.
    private static void MapCreate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/routes",
            async (UploadRouteRequest body, HttpContext ctx, WmsfoConnectionStrings connections,
                   IObjectStore store, WmsfoOptions options, CancellationToken ct) =>
            {
                ValidateUpload(body);
                var email = AdminHelpers.RequireAdminEmail(ctx);
                var obj = BuildRouteObject(body);
                return await StoreRouteAsync(obj, email, connections, store, options, ct);
            })
            .WithTags("AdminRoutes")
            .Accepts<UploadRouteRequest>("application/json")
            .Produces<RouteDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.RouteUpload)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/routes/from-event/{eventId} - build the upload body from the
    // event's published locations in seq order (contracts 4.5 Routes), then run
    // the same canonicalize / hash / lookup / PUT / insert path.
    private static void MapFromEvent(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/routes/from-event/{eventId:long}",
            async (long eventId, RouteFromEventRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, IObjectStore store, WmsfoOptions options,
                   CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 200)
                    RequestValidation.Throw("name", "must be 1 to 200 characters");
                var email = AdminHelpers.RequireAdminEmail(ctx);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                // Event existence.
                await using (var check = new NpgsqlCommand("select 1 from event where id = $1;", conn))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull)
                        throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "event not found");
                }

                // Stream the published locations. Enforce the 50,000 cap first
                // (413), then the 2-point minimum (400).
                var points = new List<RoutePoint>();
                await using (var cmd = new NpgsqlCommand(@"
select lat, lng, recorded_at
from location where event_id = $1 and published
order by seq;", conn))
                {
                    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        if (points.Count >= 50_000)
                            throw new ApiException(StatusCodes.Status413PayloadTooLarge, ApiErrorCodes.PayloadTooLarge, "over 50,000 points");
                        points.Add(new RoutePoint
                        {
                            Lat = reader.GetDouble(0),
                            Lng = reader.GetDouble(1),
                            RecordedAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                        });
                    }
                }
                if (points.Count < 2)
                    throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "fewer than 2 points");

                var obj = new RouteObject
                {
                    SchemaVersion = 1,
                    Name = body.Name.Trim(),
                    Points = points,
                };
                return await StoreRouteAsync(obj, email, connections, store, options, ct);
            })
            .WithTags("AdminRoutes")
            .Accepts<RouteFromEventRequest>("application/json")
            .Produces<RouteDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // DELETE /admin/routes/{id} - 409 route_in_use if any event references it;
    // otherwise delete the row, then the object (a failed delete of the object
    // is logged; the row is already gone).
    private static void MapDelete(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/routes/{id:long}",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, IObjectStore store,
                   ILoggerFactory loggerFactory, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                string? s3Key = null;
                await using (var read = new NpgsqlCommand("select s3_key from route where id = $1;", conn))
                {
                    read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await read.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull)
                        throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "route not found");
                    s3Key = (string)r;
                }
                await using (var refs = new NpgsqlCommand(
                    "select 1 from event where route_id = $1 limit 1;", conn))
                {
                    refs.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await refs.ExecuteScalarAsync(ct);
                    if (r is not null && r is not DBNull)
                        throw new ApiException(StatusCodes.Status409Conflict, "route_in_use", "route referenced by an event");
                }
                await using (var del = new NpgsqlCommand("delete from route where id = $1;", conn))
                {
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await del.ExecuteNonQueryAsync(ct);
                }
                try
                {
                    await store.DeleteObjectAsync(s3Key!, ct);
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger("Wmsfo.Api.Endpoints.AdminRoutes")
                        .LogWarning(ex, "route object delete failed; key={Key}", s3Key);
                }
                return Results.NoContent();
            })
            .WithTags("AdminRoutes")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- shared plumbing ---

    private static async Task<IResult> StoreRouteAsync(
        RouteObject obj,
        string email,
        WmsfoConnectionStrings connections,
        IObjectStore store,
        WmsfoOptions options,
        CancellationToken ct)
    {
        var bytes = CanonicalJson.SerializeToUtf8Bytes(obj);
        var sha = CanonicalJson.Sha256Hex(bytes);
        var key = $"routes/{sha}.json";
        var url = options.CdnBaseUrl.TrimEnd('/') + "/" + key;

        await using var conn = new NpgsqlConnection(connections.App);
        await conn.OpenAsync(ct);

        // Existing-row check - return the row without a PUT.
        await using (var lookup = new NpgsqlCommand(@"
select id, name, url, s3_key, sha256, point_count, uploaded_by, created_at
from route where s3_key = $1;", conn))
        {
            lookup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
            await using var reader = await lookup.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return Results.Ok(ReadRoute(reader));
            }
        }

        // One attempt, 3 s timeout, else 502 route_write_failed.
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PutTimeout);
            await store.PutObjectAsync(key, bytes, JsonContentType, ImmutableCacheControl, tag: null, cts.Token);
        }
        catch (Exception)
        {
            throw new ApiException(StatusCodes.Status502BadGateway, "route_write_failed", "route upload failed");
        }

        try
        {
            await using var ins = new NpgsqlCommand(@"
insert into route (name, s3_key, url, sha256, point_count, uploaded_by)
values ($1, $2, $3, $4, $5, $6)
returning id, name, url, s3_key, sha256, point_count, uploaded_by, created_at;", conn);
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = obj.Name });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = url });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = sha });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = obj.Points.Count });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
            await using var reader = await ins.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return Results.Json(ReadRoute(reader), statusCode: StatusCodes.Status201Created);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == ConstraintErrorMapping.RouteS3KeyKey)
        {
            // Concurrent identical upload: return the existing row.
            await using var read2 = new NpgsqlCommand(@"
select id, name, url, s3_key, sha256, point_count, uploaded_by, created_at
from route where s3_key = $1;", conn);
            read2.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
            await using var reader = await read2.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return Results.Ok(ReadRoute(reader));
            throw new ApiException(StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError, "route lookup failed after concurrent insert");
        }
    }

    // Route body validation (contracts 1.4).
    private static void ValidateUpload(UploadRouteRequest body)
    {
        var v = new RequestValidation();
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 200)
            v.Field("name", "must be 1 to 200 characters");
        if (body.Points is null)
            v.Field("points", "required");
        v.ThrowIfInvalid();

        if (body.Points!.Count > 50_000)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, ApiErrorCodes.PayloadTooLarge, "over 50,000 points");
        if (body.Points.Count < 2)
            RequestValidation.Throw("points", "must have at least 2 items");

        for (var i = 0; i < body.Points.Count; i++)
        {
            var p = body.Points[i];
            if (double.IsNaN(p.Lat) || double.IsInfinity(p.Lat) || p.Lat < -90 || p.Lat > 90)
                RequestValidation.Throw($"points[{i}].lat", "must be between -90 and 90");
            if (double.IsNaN(p.Lng) || double.IsInfinity(p.Lng) || p.Lng < -180 || p.Lng > 180)
                RequestValidation.Throw($"points[{i}].lng", "must be between -180 and 180");
        }
    }

    private static RouteObject BuildRouteObject(UploadRouteRequest body) => new()
    {
        SchemaVersion = 1,
        Name = body.Name.Trim(),
        Points = body.Points.Select(p => new RoutePoint
        {
            Lat = p.Lat,
            Lng = p.Lng,
            RecordedAt = p.RecordedAt,
        }).ToList(),
    };

    private static RouteDto ReadRoute(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Url = reader.GetString(2),
        S3Key = reader.GetString(3),
        Sha256 = reader.GetString(4).Trim(),
        PointCount = reader.GetInt32(5),
        UploadedBy = reader.GetString(6),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
    };
}
