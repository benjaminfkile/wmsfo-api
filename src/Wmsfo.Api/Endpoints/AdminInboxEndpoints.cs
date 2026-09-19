using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Email;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 Contact messages, subscribers, people (Admin), plus the Email
// quota (Admin) endpoint that reads SES v2 GetAccount through the shared
// IEmailQuotaReader (api.md 13) and counts the unsent alert_delivery rows and
// verified email subscribers so the panel can warn before a send passes the
// 24 h cap. This endpoint reads only; there is no audit row.
// - GET paged lists (cursor is base64url of the last id, descending)
// - GET /admin/subscribers/summary (counts across the three states)
// - GET /admin/email/quota (the SES numbers plus queued + verifiedSubscribers)
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
        MapEmailQuota(app);
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
                if (cursor is not null) conditions.Add($"m.id < ${next++}");
                var limitIdx = next;
                var sql = @"
select m.id, m.name, m.email, m.body, m.client_ip, m.created_at,
       a.action, a.actor, a.at
from contact_message m
left join lateral (
  select action, actor, at from audit_log
  where entity = 'contact_message' and entity_id = m.id::text
  order by id desc limit 1
) a on true"
                    + (conditions.Count > 0 ? " where " + string.Join(" and ", conditions) : "")
                    + " order by m.id desc limit $" + limitIdx + ";";
                await using var cmd = new NpgsqlCommand(sql, conn);
                if (cursor is not null) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cursor.Value });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit + 1 });
                var items = new List<ContactMessageDto>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var dto = new ContactMessageDto
                    {
                        Id = reader.GetInt64(0),
                        Name = reader.GetString(1),
                        Email = reader.GetString(2),
                        Body = reader.GetString(3),
                        ClientIp = reader.GetString(4),
                        CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                    };
                    if (!reader.IsDBNull(6))
                    {
                        dto.Audit = new AuditStampDto
                        {
                            Action = reader.GetString(6),
                            By = reader.GetString(7),
                            At = reader.GetFieldValue<DateTimeOffset>(8),
                        };
                    }
                    items.Add(dto);
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
            async (long id, HttpContext ctx, AuditRecorder audit, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                ContactMessageDto? before = null;
                await using (var read = new NpgsqlCommand(
                    "select id, name, email, body, client_ip, created_at from contact_message where id = $1 for update;", conn, tx))
                {
                    read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        before = new ContactMessageDto
                        {
                            Id = reader.GetInt64(0),
                            Name = reader.GetString(1),
                            Email = reader.GetString(2),
                            Body = reader.GetString(3),
                            ClientIp = reader.GetString(4),
                            CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                        };
                    }
                }
                if (before is null)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "contact message not found");
                var impact = await Impact.ContactMessageImpactQueries.PreviewAsync(conn, tx, id, ct);
                await Impact.ContactMessageImpactQueries.ApplyAsync(conn, tx, id, ct);
                await using (var del = new NpgsqlCommand(
                    "delete from contact_message where id = $1;", conn, tx))
                {
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await del.ExecuteNonQueryAsync(ct);
                }
                await audit.RecordAsync(conn, tx, "delete", "contact_message",
                    id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    before: Impact.ImpactBefore.Combine(before, impact), after: null, ct);
                await tx.CommitAsync(ct);
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
       s.person_id, coalesce(p.email, ''),
       a.action, a.actor, a.at
from subscriber s
left join person p on p.id = s.person_id
left join lateral (
  select action, actor, at from audit_log
  where entity = 'subscriber' and entity_id = s.id::text
  order by id desc limit 1
) a on true"
                    + (conditions.Count > 0 ? " where " + string.Join(" and ", conditions) : "")
                    + " order by s.id desc limit $" + limitIdx + ";";
                await using var cmd = new NpgsqlCommand(sql, conn);
                if (cursor is not null) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cursor.Value });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit + 1 });
                var items = new List<SubscriberAdminDto>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var dto = new SubscriberAdminDto
                    {
                        Id = reader.GetInt64(0),
                        Channel = reader.GetString(1),
                        Address = reader.GetString(2),
                        VerifiedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                        UnsubscribedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                        CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                        PersonId = reader.GetInt64(6),
                        PersonEmail = reader.GetString(7),
                    };
                    if (!reader.IsDBNull(8))
                    {
                        dto.Audit = new AuditStampDto
                        {
                            Action = reader.GetString(8),
                            By = reader.GetString(9),
                            At = reader.GetFieldValue<DateTimeOffset>(10),
                        };
                    }
                    items.Add(dto);
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

    private static void MapEmailQuota(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/email/quota",
            async (HttpContext ctx, WmsfoConnectionStrings connections,
                [Microsoft.AspNetCore.Mvc.FromServices] IEmailQuotaReader reader,
                WmsfoOptions options, CancellationToken ct) =>
            {
                var reading = await reader.ReadAsync(ct);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                int queued;
                await using (var q = new NpgsqlCommand(
                    "select count(*) from alert_delivery where sent_at is null;", conn))
                {
                    queued = (int)((long?)(await q.ExecuteScalarAsync(ct)) ?? 0L);
                }

                int verifiedSubscribers;
                await using (var v = new NpgsqlCommand(
                    "select count(*) from subscriber where channel = 'email' and verified_at is not null and unsubscribed_at is null;", conn))
                {
                    verifiedSubscribers = (int)((long?)(await v.ExecuteScalarAsync(ct)) ?? 0L);
                }

                double? remaining = null;
                var wouldExceed = false;
                if (reading.Available && reading.Max24HourSend is double max)
                {
                    var used = (reading.SentLast24Hours ?? 0.0) + queued;
                    var left = max - used;
                    if (left < 0) left = 0;
                    remaining = left;
                    wouldExceed = verifiedSubscribers > left;
                }

                var dto = new EmailQuota
                {
                    Available = reading.Available,
                    DryRun = options.SesDryRun,
                    Max24HourSend = reading.Max24HourSend,
                    SentLast24Hours = reading.SentLast24Hours,
                    MaxSendRate = reading.MaxSendRate,
                    Queued = queued,
                    Remaining = remaining,
                    VerifiedSubscribers = verifiedSubscribers,
                    WouldExceed = wouldExceed,
                    FetchedAt = DateTimeOffset.UtcNow,
                };
                return Results.Ok(dto);
            })
            .WithTags("AdminInbox")
            .Produces<EmailQuota>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .DenyApiKeys()
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapDeleteSubscriber(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/subscribers/{id:long}",
            async (long id, HttpContext ctx, AuditRecorder audit, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                // Lock the subscriber row first; FOR UPDATE cannot be applied
                // to the nullable side of the left join to `person`.
                await using (var lockCmd = new NpgsqlCommand(
                    "select 1 from subscriber where id = $1 for update;", conn, tx))
                {
                    lockCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var lockR = await lockCmd.ExecuteScalarAsync(ct);
                    if (lockR is null || lockR is DBNull)
                        throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "subscriber not found");
                }
                SubscriberAdminDto? before = null;
                await using (var read = new NpgsqlCommand(@"
select s.id, s.channel, s.address, s.verified_at, s.unsubscribed_at, s.created_at,
       s.person_id, coalesce(p.email, '')
from subscriber s left join person p on p.id = s.person_id
where s.id = $1;", conn, tx))
                {
                    read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        before = new SubscriberAdminDto
                        {
                            Id = reader.GetInt64(0),
                            Channel = reader.GetString(1),
                            Address = reader.GetString(2),
                            VerifiedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                            UnsubscribedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                            CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                            PersonId = reader.GetInt64(6),
                            PersonEmail = reader.GetString(7),
                        };
                    }
                }
                if (before is null)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "subscriber not found");
                var impact = await Impact.SubscriberImpactQueries.PreviewAsync(conn, tx, id, ct);
                await Impact.SubscriberImpactQueries.ApplyAsync(conn, tx, id, ct);
                await using (var del = new NpgsqlCommand(
                    "delete from subscriber where id = $1;", conn, tx))
                {
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await del.ExecuteNonQueryAsync(ct);
                }
                await audit.RecordAsync(conn, tx, "delete", "subscriber",
                    id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    before: Impact.ImpactBefore.Combine(before, impact), after: null, ct);
                await tx.CommitAsync(ct);
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
       coalesce((select count(*) from cookie c where c.person_id = p.id), 0),
       a.action, a.actor, a.at
from person p
left join lateral (
  select action, actor, at from audit_log
  where entity = 'person' and entity_id = p.id::text
  order by id desc limit 1
) a on true"
                    + (conditions.Count > 0 ? " where " + string.Join(" and ", conditions) : "")
                    + " order by p.id desc limit $" + limitIdx + ";";
                await using var cmd = new NpgsqlCommand(sql, conn);
                if (cursor is not null) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cursor.Value });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit + 1 });
                var items = new List<PersonWithCookieCountDto>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var dto = new PersonWithCookieCountDto
                    {
                        Id = reader.GetInt64(0),
                        Email = reader.GetString(1),
                        CreatedAt = reader.GetFieldValue<DateTimeOffset>(2),
                        LastSeenAt = reader.GetFieldValue<DateTimeOffset>(3),
                        CookieCount = (int)reader.GetInt64(4),
                    };
                    if (!reader.IsDBNull(5))
                    {
                        dto.Audit = new AuditStampDto
                        {
                            Action = reader.GetString(5),
                            By = reader.GetString(6),
                            At = reader.GetFieldValue<DateTimeOffset>(7),
                        };
                    }
                    items.Add(dto);
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
            async (long id, HttpContext ctx, AuditRecorder audit, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                PersonDto? before = null;
                await using (var read = new NpgsqlCommand(
                    "select id, email, created_at, last_seen_at from person where id = $1 for update;", conn, tx))
                {
                    read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        before = new PersonDto
                        {
                            Id = reader.GetInt64(0),
                            Email = reader.GetString(1),
                            CreatedAt = reader.GetFieldValue<DateTimeOffset>(2),
                            LastSeenAt = reader.GetFieldValue<DateTimeOffset>(3),
                        };
                    }
                }
                if (before is null)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "person not found");
                var impact = await Impact.PersonImpactQueries.PreviewAsync(conn, tx, id, ct);
                await Impact.PersonImpactQueries.ApplyAsync(conn, tx, id, ct);
                await using (var del = new NpgsqlCommand(
                    "delete from person where id = $1;", conn, tx))
                {
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await del.ExecuteNonQueryAsync(ct);
                }
                await audit.RecordAsync(conn, tx, "delete", "person",
                    id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    before: Impact.ImpactBefore.Combine(before, impact), after: null, ct);
                await tx.CommitAsync(ct);
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
