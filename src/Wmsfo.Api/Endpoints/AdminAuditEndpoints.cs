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

// contracts 4.5 Audit (Editor). GET /admin/audit is a keyset-paged listing
// newest first (id desc) with equality filters on entity, entityId, action,
// actor. GET /admin/audit/entities returns the distinct entity kinds the log
// knows. Both are read-only; editors may read (api.md 5a); the audit capability
// grants an API key the same access.
public static class AdminAuditEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapEntities(app);
    }

    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/audit",
            async (HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var q = ctx.Request.Query;
                var entity = q["entity"].ToString();
                var entityId = q["entityId"].ToString();
                var action = q["action"].ToString();
                var actor = q["actor"].ToString();
                var cursor = AdminHelpers.DecodeLongCursor(string.IsNullOrEmpty(q["cursor"].ToString()) ? null : q["cursor"].ToString());
                var limitText = q["limit"].ToString();
                int? rawLimit = null;
                if (!string.IsNullOrEmpty(limitText))
                {
                    if (!int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "limit malformed");
                    if (parsed < 1 || parsed > 200)
                        throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "limit must be between 1 and 200");
                    rawLimit = parsed;
                }
                var limit = rawLimit ?? 50;

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var conditions = new List<string>();
                var parameters = new List<NpgsqlParameter>();
                var next = 1;
                if (!string.IsNullOrEmpty(entity))
                {
                    conditions.Add($"entity = ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = entity });
                }
                if (!string.IsNullOrEmpty(entityId))
                {
                    conditions.Add($"entity_id = ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = entityId });
                }
                if (!string.IsNullOrEmpty(action))
                {
                    conditions.Add($"action = ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = action });
                }
                if (!string.IsNullOrEmpty(actor))
                {
                    conditions.Add($"actor = ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = actor });
                }
                if (cursor is not null)
                {
                    conditions.Add($"id < ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cursor.Value });
                }
                var limitIdx = next;
                var sql = "select id, at, actor, action, entity, entity_id, before, after, request_id from audit_log"
                    + (conditions.Count > 0 ? " where " + string.Join(" and ", conditions) : "")
                    + $" order by id desc limit ${limitIdx};";
                await using var cmd = new NpgsqlCommand(sql, conn);
                foreach (var p in parameters) cmd.Parameters.Add(p);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit + 1 });
                var items = new List<AuditEntryDto>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new AuditEntryDto
                    {
                        Id = reader.GetInt64(0),
                        At = reader.GetFieldValue<DateTimeOffset>(1),
                        Actor = reader.GetString(2),
                        Action = reader.GetString(3),
                        Entity = reader.GetString(4),
                        EntityId = reader.GetString(5),
                        Before = reader.IsDBNull(6) ? null : ParseJson(reader.GetString(6)),
                        After = reader.IsDBNull(7) ? null : ParseJson(reader.GetString(7)),
                        RequestId = reader.IsDBNull(8) ? null : reader.GetString(8),
                    });
                }
                string? nextCursor = null;
                if (items.Count > limit)
                {
                    nextCursor = AdminHelpers.EncodeLongCursor(items[limit - 1].Id);
                    items.RemoveRange(limit, items.Count - limit);
                }
                return Results.Ok(new PageResponse<AuditEntryDto> { Items = items, NextCursor = nextCursor });
            })
            .WithTags("AdminAudit")
            .Produces<PageResponse<AuditEntryDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireCapability(ApiKeyCapabilities.Audit)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapEntities(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/audit/entities",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("select distinct entity from audit_log order by entity;", conn);
                var items = new List<string>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) items.Add(reader.GetString(0));
                return Results.Ok(new ItemsResponse<string> { Items = items });
            })
            .WithTags("AdminAudit")
            .Produces<ItemsResponse<string>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireCapability(ApiKeyCapabilities.Audit)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static JsonElement? ParseJson(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
