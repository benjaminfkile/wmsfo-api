using System.Globalization;
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

// contracts 4.5 Contact messages, subscribers, people (Admin).
// - GET paged lists (cursor is base64url of the last id, descending)
// - GET /admin/subscribers/summary (counts across the three states)
// - GET /admin/people (with cookieCount across every event)
// - DELETE row is a hard delete (people cascade to their subscribers and cookies)
public static class AdminInboxEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapListContactMessages(app);
        MapDeleteContactMessage(app);
        MapListSubscribers(app);
        MapSubscribersSummary(app);
        MapDeleteSubscriber(app);
        MapListPeople(app);
        MapDeletePerson(app);
    }

    private static void MapListContactMessages(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/contact-messages",
            async (HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var cursor = ParseCursor(ctx);
                var limit = ParseLimit(ctx, defaultLimit: 100);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                var conditions = new List<string>();
                var next = 1;
                if (cursor is not null) conditions.Add($"id < ${next++}");
                var limitIdx = next;
                var sql = "select id, name, email, body, client_ip, created_at from contact_message"
                    + (conditions.Count > 0 ? " where " + string.Join(" and ", conditions) : "")
                    + " order by id desc limit $" + limitIdx + ";";
                await using var cmd = new NpgsqlCommand(sql, conn);
                if (cursor is not null) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cursor.Value });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit + 1 });
                var items = new List<ContactMessageDto>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new ContactMessageDto
                    {
                        Id = reader.GetInt64(0),
                        Name = reader.GetString(1),
                        Email = reader.GetString(2),
                        Body = reader.GetString(3),
                        ClientIp = reader.GetString(4),
                        CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                    });
                }
                string? nextCursor = null;
                if (items.Count > limit)
                {
                    nextCursor = AdminHelpers.EncodeLongCursor(items[limit - 1].Id);
                    items.RemoveRange(limit, items.Count - limit);
                }
                return Results.Ok(new PageResponse<ContactMessageDto> { Items = items, NextCursor = nextCursor });
            })
            .WithTags("AdminInbox")
            .Produces<PageResponse<ContactMessageDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.ContactMessages)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapDeleteContactMessage(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/contact-messages/{id:long}",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    "delete from contact_message where id = $1 returning id;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                var r = await cmd.ExecuteScalarAsync(ct);
                if (r is null || r is DBNull)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "contact message not found");
                return Results.NoContent();
            })
            .WithTags("AdminInbox")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.ContactMessages)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapListSubscribers(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/subscribers",
            async (HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var cursor = ParseCursor(ctx);
                var limit = ParseLimit(ctx, defaultLimit: 100);
                var status = (ctx.Request.Query["status"].ToString() ?? "").Trim();
                string? statusFilter = null;
                if (!string.IsNullOrEmpty(status))
                {
                    switch (status)
                    {
                        case "verified":
                            statusFilter = "s.verified_at is not null and s.unsubscribed_at is null";
                            break;
                        case "pending":
                            statusFilter = "s.verified_at is null and s.unsubscribed_at is null";
                            break;
                        case "unsubscribed":
                            statusFilter = "s.unsubscribed_at is not null";
                            break;
                        default:
                            throw new ApiException(StatusCodes.Status400BadRequest,
                                ApiErrorCodes.ValidationFailed,
                                "status must be verified, pending, or unsubscribed");
                    }
                }

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                var conditions = new List<string>();
                var next = 1;
                if (cursor is not null) conditions.Add($"s.id < ${next++}");
                if (statusFilter is not null) conditions.Add(statusFilter);
                var limitIdx = next;
                var sql = @"
select s.id, s.channel, s.address, s.verified_at, s.unsubscribed_at, s.created_at,
       s.person_id, coalesce(p.email, '')
from subscriber s
left join person p on p.id = s.person_id"
                    + (conditions.Count > 0 ? " where " + string.Join(" and ", conditions) : "")
                    + " order by s.id desc limit $" + limitIdx + ";";
                await using var cmd = new NpgsqlCommand(sql, conn);
                if (cursor is not null) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cursor.Value });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit + 1 });
                var items = new List<SubscriberAdminDto>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new SubscriberAdminDto
                    {
                        Id = reader.GetInt64(0),
                        Channel = reader.GetString(1),
                        Address = reader.GetString(2),
                        VerifiedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                        UnsubscribedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                        CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                        PersonId = reader.GetInt64(6),
                        PersonEmail = reader.GetString(7),
                    });
                }
                string? nextCursor = null;
                if (items.Count > limit)
                {
                    nextCursor = AdminHelpers.EncodeLongCursor(items[limit - 1].Id);
                    items.RemoveRange(limit, items.Count - limit);
                }
                return Results.Ok(new PageResponse<SubscriberAdminDto> { Items = items, NextCursor = nextCursor });
            })
            .WithTags("AdminInbox")
            .Produces<PageResponse<SubscriberAdminDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Subscribers)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapSubscribersSummary(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/subscribers/summary",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                int verified = 0, pending = 0, unsubscribed = 0;
                await using var cmd = new NpgsqlCommand(@"
select
  count(*) filter (where verified_at is not null and unsubscribed_at is null),
  count(*) filter (where verified_at is null and unsubscribed_at is null),
  count(*) filter (where unsubscribed_at is not null)
from subscriber;", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    verified = (int)reader.GetInt64(0);
                    pending = (int)reader.GetInt64(1);
                    unsubscribed = (int)reader.GetInt64(2);
                }
                return Results.Ok(new SubscribersSummaryResponse
                {
                    Verified = verified,
                    Pending = pending,
                    Unsubscribed = unsubscribed,
                });
            })
            .WithTags("AdminInbox")
            .Produces<SubscribersSummaryResponse>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Subscribers)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapDeleteSubscriber(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/subscribers/{id:long}",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    "delete from subscriber where id = $1 returning id;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                var r = await cmd.ExecuteScalarAsync(ct);
                if (r is null || r is DBNull)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "subscriber not found");
                return Results.NoContent();
            })
            .WithTags("AdminInbox")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Subscribers)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapListPeople(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/people",
            async (HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var cursor = ParseCursor(ctx);
                var limit = ParseLimit(ctx, defaultLimit: 100);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                var conditions = new List<string>();
                var next = 1;
                if (cursor is not null) conditions.Add($"p.id < ${next++}");
                var limitIdx = next;
                var sql = @"
select p.id, p.email, p.created_at, p.last_seen_at,
       coalesce((select count(*) from cookie c where c.person_id = p.id), 0)
from person p"
                    + (conditions.Count > 0 ? " where " + string.Join(" and ", conditions) : "")
                    + " order by p.id desc limit $" + limitIdx + ";";
                await using var cmd = new NpgsqlCommand(sql, conn);
                if (cursor is not null) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cursor.Value });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit + 1 });
                var items = new List<PersonWithCookieCountDto>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new PersonWithCookieCountDto
                    {
                        Id = reader.GetInt64(0),
                        Email = reader.GetString(1),
                        CreatedAt = reader.GetFieldValue<DateTimeOffset>(2),
                        LastSeenAt = reader.GetFieldValue<DateTimeOffset>(3),
                        CookieCount = (int)reader.GetInt64(4),
                    });
                }
                string? nextCursor = null;
                if (items.Count > limit)
                {
                    nextCursor = AdminHelpers.EncodeLongCursor(items[limit - 1].Id);
                    items.RemoveRange(limit, items.Count - limit);
                }
                return Results.Ok(new PageResponse<PersonWithCookieCountDto> { Items = items, NextCursor = nextCursor });
            })
            .WithTags("AdminInbox")
            .Produces<PageResponse<PersonWithCookieCountDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.People)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapDeletePerson(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/people/{id:long}",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    "delete from person where id = $1 returning id;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                var r = await cmd.ExecuteScalarAsync(ct);
                if (r is null || r is DBNull)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "person not found");
                return Results.NoContent();
            })
            .WithTags("AdminInbox")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.People)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static long? ParseCursor(HttpContext ctx)
    {
        var text = ctx.Request.Query["cursor"].ToString();
        return string.IsNullOrEmpty(text) ? null : AdminHelpers.DecodeLongCursor(text);
    }

    private static int ParseLimit(HttpContext ctx, int defaultLimit)
    {
        var text = ctx.Request.Query["limit"].ToString();
        if (string.IsNullOrEmpty(text)) return defaultLimit;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "limit malformed");
        return AdminHelpers.ClampLimit(parsed, defaultLimit);
    }
}
