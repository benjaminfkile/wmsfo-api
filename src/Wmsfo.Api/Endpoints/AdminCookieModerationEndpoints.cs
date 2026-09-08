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
using Wmsfo.Api.Node;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 Cookies (moderation, Admin). Allowed in any event status.
// Hide/unhide are autocommit updates; delete is a hard delete. No snapshot
// rebuild. When the cookie belongs to a status-3 (live) event, the node re-reads
// the tally from SQL and writes the live object. In any other status, SQL only.
public static class AdminCookieModerationEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapHide(app);
        MapUnhide(app);
        MapDelete(app);
    }

    private static void MapHide(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/cookies/{id:long}/hide",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections,
                   NodeStateService state, LiveObjectWriter writer, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                var (dto, wasLive) = await ApplyAsync(connections, id, hide: true, delete: false, email, ct);
                if (dto is null)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "cookie not found");
                if (wasLive) FireLiveWrite(state, writer);
                return Results.Ok(dto);
            })
            .WithTags("AdminCookies")
            .Produces<CookieAdminDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapUnhide(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/cookies/{id:long}/unhide",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections,
                   NodeStateService state, LiveObjectWriter writer, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                var (dto, wasLive) = await ApplyAsync(connections, id, hide: false, delete: false, email, ct);
                if (dto is null)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "cookie not found");
                if (wasLive) FireLiveWrite(state, writer);
                return Results.Ok(dto);
            })
            .WithTags("AdminCookies")
            .Produces<CookieAdminDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapDelete(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/cookies/{id:long}",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections,
                   NodeStateService state, LiveObjectWriter writer, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                var (dto, wasLive) = await ApplyAsync(connections, id, hide: false, delete: true, adminEmail: "", ct);
                if (dto is null)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "cookie not found");
                if (wasLive) FireLiveWrite(state, writer);
                return Results.NoContent();
            })
            .WithTags("AdminCookies")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static async Task<(CookieAdminDto? dto, bool wasLive)> ApplyAsync(
        WmsfoConnectionStrings connections, long id, bool hide, bool delete, string adminEmail, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connections.App);
        await conn.OpenAsync(ct);

        // Read the cookie's event status_id so we can decide whether to write the
        // live object after the mutation. The read is outside the mutation to keep
        // the moderation statements autocommit as sql.md 8.10 spells them.
        short? statusId = null;
        long? eventId = null;
        await using (var ev = new NpgsqlCommand(@"
select c.event_id, e.status_id
from cookie c
join event e on e.id = c.event_id
where c.id = $1;", conn))
        {
            ev.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await ev.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                eventId = reader.GetInt64(0);
                statusId = reader.GetInt16(1);
            }
        }
        if (eventId is null)
        {
            return (null, false);
        }

        CookieAdminDto? dto = null;
        if (delete)
        {
            await using var d = new NpgsqlCommand("delete from cookie where id = $1 returning id;", conn);
            d.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            var r = await d.ExecuteScalarAsync(ct);
            if (r is null || r is DBNull) return (null, false);
            dto = new CookieAdminDto { Id = id, EventId = eventId.Value };
        }
        else
        {
            var sql = hide
                ? "update cookie set hidden_at = now(), hidden_by = $1 where id = $2 returning id, event_id, person_id, cookie_type_id, note, left_at, hidden_at, hidden_by;"
                : "update cookie set hidden_at = null, hidden_by = null where id = $1 returning id, event_id, person_id, cookie_type_id, note, left_at, hidden_at, hidden_by;";
            await using var upd = new NpgsqlCommand(sql, conn);
            if (hide)
            {
                upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = adminEmail });
                upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            }
            else
            {
                upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            }
            long personId;
            long resultId, resultEventId, cookieTypeId;
            string? note;
            DateTimeOffset leftAt;
            DateTimeOffset? hiddenAt;
            string? hiddenBy;
            await using (var reader = await upd.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return (null, false);
                resultId = reader.GetInt64(0);
                resultEventId = reader.GetInt64(1);
                personId = reader.GetInt64(2);
                cookieTypeId = reader.GetInt64(3);
                note = reader.IsDBNull(4) ? null : reader.GetString(4);
                leftAt = reader.GetFieldValue<DateTimeOffset>(5);
                hiddenAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6);
                hiddenBy = reader.IsDBNull(7) ? null : reader.GetString(7);
            }
            var email = await LookupPersonEmailAsync(conn, personId, ct);
            dto = new CookieAdminDto
            {
                Id = resultId,
                EventId = resultEventId,
                PersonId = personId,
                PersonEmail = email,
                CookieTypeId = cookieTypeId,
                Note = note,
                LeftAt = leftAt,
                HiddenAt = hiddenAt,
                HiddenBy = hiddenBy,
            };
        }

        return (dto, statusId == 3);
    }

    private static async Task<string> LookupPersonEmailAsync(NpgsqlConnection conn, long personId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select email from person where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is string s ? s : "";
    }

    // sql.md 8.10: after the moderation write, when the cookie's event is live the
    // node refreshes state and writes the live object. This is fire-and-forget so
    // the admin response is not held up by the CDN write.
    private static void FireLiveWrite(NodeStateService state, LiveObjectWriter writer)
    {
        _ = writer.WriteFromStateAsync("moderation", CancellationToken.None);
    }
}
