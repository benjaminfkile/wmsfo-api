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
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 Events (Admin) endpoints. Every write marked [snapshot] runs
// the frame in sql.md 8.5 / api.md 10.2; the status change and message writes
// also insert their outbox rows (contracts 7.7). Every read is a plain select
// with no snapshot rebuild.
public static class AdminEventEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapCreate(app);
        MapGet(app);
        MapPatch(app);
        MapDelete(app);
        MapCurrent(app);
        MapStatus(app);
        MapStatusHistory(app);
        MapMessages(app);
        MapCreateMessage(app);
        MapPatchMessage(app);
        MapDeleteMessage(app);
        MapLocations(app);
        MapCookies(app);
    }

    // GET /admin/events → 200 { items: Event[] } ordered by year desc.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/events",
            async (WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<EventDto>();
                await using var cmd = new NpgsqlCommand(EventSelectSql + " order by e.year desc, e.id desc;", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(ReadEvent(reader, options));
                }
                return Results.Ok(new ItemsResponse<EventDto> { Items = items });
            })
            .WithTags("AdminEvents")
            .Produces<ItemsResponse<EventDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/events [snapshot]. inheritRoute: true copies the greatest
    // year's route_id (or null); inheritRoute: false uses the supplied routeId.
    // year unique, name 1..200, fundsPercent 0..100.
    private static void MapCreate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/events",
            async (CreateEventRequest body, HttpContext ctx, AdminSnapshotTransaction snap, WmsfoOptions options, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (body.Year < 2000 || body.Year > 2100) v.Field("year", "must be between 2000 and 2100");
                if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 200) v.Field("name", "must be 1 to 200 characters");
                if (body.FundsPercent < 0 || body.FundsPercent > 100) v.Field("fundsPercent", "must be between 0 and 100");
                if (body.InheritRoute && body.RouteId is not null) v.Field("routeId", "must be null when inheritRoute is true");
                v.ThrowIfInvalid();

                var email = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<EventDto>(async (conn, tx, token) =>
                {
                    long? routeId = body.RouteId;
                    if (body.InheritRoute)
                    {
                        await using var pick = new NpgsqlCommand(@"
select route_id from event where route_id is not null order by year desc limit 1;", conn, tx);
                        var r = await pick.ExecuteScalarAsync(token);
                        routeId = r is null || r is DBNull ? null : Convert.ToInt64(r);
                    }
                    else if (routeId is not null)
                    {
                        await using var check = new NpgsqlCommand("select 1 from route where id = $1;", conn, tx);
                        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = routeId.Value });
                        var r = await check.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull)
                            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "route not found");
                    }

                    long newId;
                    await using (var insert = new NpgsqlCommand(@"
insert into event (year, name, status_id, scheduled_at, funds_percent, route_id, created_by, updated_at)
values ($1, $2, 1, $3, $4, $5, $6, now())
returning id;", conn, tx))
                    {
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = body.Year });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Name.Trim() });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)body.ScheduledAt?.ToUniversalTime() ?? DBNull.Value });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = body.FundsPercent });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)routeId ?? DBNull.Value });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                        newId = (long)(await insert.ExecuteScalarAsync(token) ?? 0L);
                    }
                    var e = await ReadEventByIdAsync(conn, tx, newId, options, token);
                    if (e is null) throw NotFound();
                    return e;
                }, ct);
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminEvents")
            .Accepts<CreateEventRequest>("application/json")
            .Produces<EventDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // GET /admin/events/{id} → 200 Event.
    private static void MapGet(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/events/{id:long}",
            async (long id, WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var dto = await ReadEventByIdAsync(conn, null, id, options, ct);
                if (dto is null) throw NotFound();
                return Results.Ok(dto);
            })
            .WithTags("AdminEvents")
            .Produces<EventDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PATCH /admin/events/{id} [snapshot]. Any of name, year, scheduledAt,
    // wentLiveAt, endedAt, fundsPercent, routeId, routeImageMediaId.
    // scheduled_at cannot be null while status_id = 2 (409 scheduled_at_required).
    // routeImageMediaId "" clears the link; a uuid must name a ready raster asset
    // (404 media, 409 media_not_ready, 400 validation_failed for svg or gif).
    private static void MapPatch(IEndpointRouteBuilder app)
    {
        app.MapPatch("/admin/events/{id:long}",
            async (long id, PatchEventRequest body, HttpContext ctx,
                   AdminSnapshotTransaction snap, WmsfoOptions options, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (body.Year is int year && (year < 2000 || year > 2100)) v.Field("year", "must be between 2000 and 2100");
                if (body.Name is not null && (body.Name.Length < 1 || body.Name.Length > 200)) v.Field("name", "must be 1 to 200 characters");
                if (body.FundsPercent is int fp && (fp < 0 || fp > 100)) v.Field("fundsPercent", "must be between 0 and 100");
                Guid? parsedRouteImage = null;
                bool clearRouteImage = false;
                bool setRouteImage = false;
                if (body.RouteImageMediaId is not null)
                {
                    if (body.RouteImageMediaId.Length == 0)
                    {
                        clearRouteImage = true;
                    }
                    else if (Guid.TryParse(body.RouteImageMediaId, out var g))
                    {
                        parsedRouteImage = g;
                        setRouteImage = true;
                    }
                    else
                    {
                        v.Field("routeImageMediaId", "must be a uuid, empty string, or null");
                    }
                }
                v.ThrowIfInvalid();
                _ = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<EventDto>(async (conn, tx, token) =>
                {
                    short currentStatus = 0;
                    DateTimeOffset? currentScheduled = null;
                    await using (var read = new NpgsqlCommand(
                        "select status_id, scheduled_at from event where id = $1 for update;", conn, tx))
                    {
                        read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await using var reader = await read.ExecuteReaderAsync(token);
                        if (!await reader.ReadAsync(token)) throw NotFound();
                        currentStatus = reader.GetInt16(0);
                        currentScheduled = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
                    }

                    // Route existence check.
                    if (body.RouteId is not null)
                    {
                        await using var check = new NpgsqlCommand("select 1 from route where id = $1;", conn, tx);
                        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.RouteId.Value });
                        var r = await check.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull)
                            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "route not found");
                    }

                    // Route image media asset check (ready, raster).
                    if (setRouteImage && parsedRouteImage is not null)
                    {
                        string? state = null;
                        string? kind = null;
                        await using (var read = new NpgsqlCommand(
                            "select state, kind from media_asset where id = $1;", conn, tx))
                        {
                            read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = parsedRouteImage.Value });
                            await using var reader = await read.ExecuteReaderAsync(token);
                            if (!await reader.ReadAsync(token))
                                throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "media not found");
                            state = reader.GetString(0);
                            kind = reader.GetString(1);
                        }
                        if (!string.Equals(state, "ready", StringComparison.Ordinal))
                        {
                            throw new ApiException(StatusCodes.Status409Conflict,
                                "media_not_ready", "media asset is not ready");
                        }
                        if (!string.Equals(kind, "raster", StringComparison.Ordinal))
                        {
                            var vv = new RequestValidation();
                            vv.Field("routeImageMediaId", "must reference a raster media asset (svg and gif not allowed)");
                            vv.ThrowIfInvalid();
                        }
                    }

                    // scheduled_at null while status = 2 → 409 scheduled_at_required.
                    var effectiveScheduled = body.ScheduledAt is null && currentScheduled is null ? null
                        : body.ScheduledAt?.ToUniversalTime() ?? currentScheduled;
                    if (currentStatus == 2 && effectiveScheduled is null)
                    {
                        throw new ApiException(StatusCodes.Status409Conflict, "scheduled_at_required", "scheduled_at required for status 2");
                    }

                    var sets = new List<string>();
                    var parameters = new List<NpgsqlParameter>();
                    void Set(string sql, NpgsqlParameter p) { sets.Add(sql); parameters.Add(p); }
                    var next = 1;
                    if (body.Name is not null) Set($"name = ${next++}", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Name.Trim() });
                    if (body.Year is not null) Set($"year = ${next++}", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = body.Year.Value });
                    if (body.ScheduledAt is not null) Set($"scheduled_at = ${next++}", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = body.ScheduledAt.Value.ToUniversalTime() });
                    if (body.WentLiveAt is not null) Set($"went_live_at = ${next++}", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = body.WentLiveAt.Value.ToUniversalTime() });
                    if (body.EndedAt is not null) Set($"ended_at = ${next++}", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = body.EndedAt.Value.ToUniversalTime() });
                    if (body.FundsPercent is not null) Set($"funds_percent = ${next++}", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = body.FundsPercent.Value });
                    if (body.RouteId is not null) Set($"route_id = ${next++}", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.RouteId.Value });
                    if (setRouteImage)
                    {
                        Set($"route_image_media_id = ${next++}", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = parsedRouteImage!.Value });
                    }
                    else if (clearRouteImage)
                    {
                        sets.Add("route_image_media_id = null");
                    }

                    sets.Add("updated_at = now()");
                    var setClause = string.Join(", ", sets);
                    var whereIdIndex = next;
                    await using (var update = new NpgsqlCommand(
                        $"update event set {setClause} where id = ${whereIdIndex};", conn, tx))
                    {
                        foreach (var p in parameters) update.Parameters.Add(p);
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await update.ExecuteNonQueryAsync(token);
                    }

                    var updated = await ReadEventByIdAsync(conn, tx, id, options, token);
                    if (updated is null) throw NotFound();
                    return updated;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminEvents")
            .Accepts<PatchEventRequest>("application/json")
            .Produces<EventDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // DELETE /admin/events/{id} [snapshot]. 409 event_live (status 3), 409
    // event_has_locations (any location row), else cascade messages, cookies,
    // status history.
    private static void MapDelete(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/events/{id:long}",
            async (long id, HttpContext ctx, AdminSnapshotTransaction snap, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await snap.RunAsync<object?>(async (conn, tx, token) =>
                {
                    short status = 0;
                    await using (var read = new NpgsqlCommand(
                        "select status_id from event where id = $1 for update;", conn, tx))
                    {
                        read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r = await read.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull) throw NotFound();
                        status = Convert.ToInt16(r);
                    }
                    if (status == 3)
                        throw new ApiException(StatusCodes.Status409Conflict, "event_live", "event is live");

                    await using (var locs = new NpgsqlCommand(
                        "select 1 from location where event_id = $1 limit 1;", conn, tx))
                    {
                        locs.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r = await locs.ExecuteScalarAsync(token);
                        if (r is not null && r is not DBNull)
                            throw new ApiException(StatusCodes.Status409Conflict, "event_has_locations", "event has stored locations");
                    }

                    await using (var del = new NpgsqlCommand(
                        "delete from event where id = $1;", conn, tx))
                    {
                        del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await del.ExecuteNonQueryAsync(token);
                    }
                    return null;
                }, ct);
                return Results.NoContent();
            })
            .WithTags("AdminEvents")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/events/{id}/current [snapshot]. Idempotent on the already
    // current event - no snapshot rebuild, no live-object write.
    private static void MapCurrent(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/events/{id:long}/current",
            async (long id, HttpContext ctx, AdminSnapshotTransaction snap,
                   WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);

                // Fast path: already current → return the row with no rebuild.
                await using (var conn = new NpgsqlConnection(connections.App))
                {
                    await conn.OpenAsync(ct);
                    await using var read = new NpgsqlCommand("select is_current from event where id = $1;", conn);
                    read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await read.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound();
                    if ((bool)r)
                    {
                        var dto = await ReadEventByIdAsync(conn, null, id, options, ct);
                        return Results.Ok(dto);
                    }
                }

                var (result, _) = await snap.RunAsync<EventDto>(async (conn, tx, token) =>
                {
                    await using (var read = new NpgsqlCommand(
                        "select is_current from event where id = $1 for update;", conn, tx))
                    {
                        read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r = await read.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull) throw NotFound();
                        if ((bool)r)
                        {
                            var same = await ReadEventByIdAsync(conn, tx, id, options, token);
                            if (same is null) throw NotFound();
                            return same;
                        }
                    }

                    // 409 current_event_live if any other event is current AND live.
                    await using (var guard = new NpgsqlCommand(
                        "select 1 from event where is_current and status_id = 3 and id <> $1;", conn, tx))
                    {
                        guard.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r = await guard.ExecuteScalarAsync(token);
                        if (r is not null && r is not DBNull)
                            throw new ApiException(StatusCodes.Status409Conflict,
                                "current_event_live", "another event is current and live");
                    }

                    // Clear existing current, then set this one - two statements
                    // per sql.md 4.2 so the partial unique index is satisfied.
                    await using (var clr = new NpgsqlCommand(
                        "update event set is_current = false, updated_at = now() where is_current and id <> $1;", conn, tx))
                    {
                        clr.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await clr.ExecuteNonQueryAsync(token);
                    }
                    await using (var set = new NpgsqlCommand(
                        "update event set is_current = true, updated_at = now() where id = $1;", conn, tx))
                    {
                        set.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await set.ExecuteNonQueryAsync(token);
                    }

                    var dto = await ReadEventByIdAsync(conn, tx, id, options, token);
                    if (dto is null) throw NotFound();
                    return dto;
                }, ct);
                return Results.Ok(result);
            })
            .WithTags("AdminEvents")
            .Produces<EventDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/events/{id}/status [snapshot]. Rules per contracts 4.5.
    // Stamps went_live_at on every entry into 3 and ended_at on every entry
    // into 4; sets final_cookie_tally on entry into 4 and clears it on exit.
    // Inserts the event_status_history row and the outbox row.
    private static void MapStatus(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/events/{id:long}/status",
            async (long id, ChangeEventStatusRequest body, HttpContext ctx,
                   AdminSnapshotTransaction snap, WmsfoOptions options, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (body.StatusId < 1 || body.StatusId > 5) v.Field("statusId", "must be 1..5");
                v.ThrowIfInvalid();
                var email = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<EventDto>(async (conn, tx, token) =>
                {
                    short from = 0;
                    bool isCurrent = false;
                    DateTimeOffset? scheduledAt = null;
                    await using (var read = new NpgsqlCommand(
                        "select status_id, is_current, scheduled_at from event where id = $1 for update;", conn, tx))
                    {
                        read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await using var reader = await read.ExecuteReaderAsync(token);
                        if (!await reader.ReadAsync(token)) throw NotFound();
                        from = reader.GetInt16(0);
                        isCurrent = reader.GetBoolean(1);
                        scheduledAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
                    }

                    var to = (short)body.StatusId;
                    if (from == to)
                        throw new ApiException(StatusCodes.Status409Conflict, "event_status_unchanged", "status unchanged");
                    if (to == 3 && !isCurrent)
                        throw new ApiException(StatusCodes.Status409Conflict, "event_not_current", "event is not current");
                    if (to == 2 && scheduledAt is null)
                        throw new ApiException(StatusCodes.Status409Conflict, "scheduled_at_required", "scheduled_at required for status 2");
                    if (to == 3)
                    {
                        await using var guard = new NpgsqlCommand(
                            "select 1 from event where status_id = 3 and id <> $1;", conn, tx);
                        guard.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r = await guard.ExecuteScalarAsync(token);
                        if (r is not null && r is not DBNull)
                            throw new ApiException(StatusCodes.Status409Conflict, "another_event_live", "another event is live");
                    }

                    // On entry into 4: final_cookie_tally = jsonb_object_agg of
                    // the current non-hidden counts. On exit from 4: null.
                    string finalTallyFragment = "final_cookie_tally";
                    var extraParams = new List<NpgsqlParameter>();
                    var next = 1;
                    var idParamIndex = next++;
                    if (to == 4)
                    {
                        finalTallyFragment = @"coalesce((
    select jsonb_object_agg(cookie_type_id::text, cnt)
    from (
      select cookie_type_id, count(*)::int as cnt
      from cookie where event_id = $" + idParamIndex + @" and hidden_at is null
      group by cookie_type_id
    ) t
), '{}'::jsonb)";
                    }
                    else if (from == 4)
                    {
                        finalTallyFragment = "null::jsonb";
                    }

                    var toParamIndex = next++;
                    // update event ... set status_id = $to, went_live_at, ended_at, final_cookie_tally
                    var sql = @"
update event
set status_id = $" + toParamIndex + @",
    went_live_at = case when $" + toParamIndex + @" = 3 then now() else went_live_at end,
    ended_at = case when $" + toParamIndex + @" = 4 then now() else ended_at end,
    final_cookie_tally = " + finalTallyFragment + @",
    updated_at = now()
where id = $" + idParamIndex + @";";
                    await using (var upd = new NpgsqlCommand(sql, conn, tx))
                    {
                        upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = to });
                        foreach (var p in extraParams) upd.Parameters.Add(p);
                        try
                        {
                            await upd.ExecuteNonQueryAsync(token);
                        }
                        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == ConstraintErrorMapping.EventOneLive)
                        {
                            throw new ApiException(StatusCodes.Status409Conflict, "another_event_live", "another event is live");
                        }
                    }

                    // event_status_history row.
                    await using (var hist = new NpgsqlCommand(@"
insert into event_status_history (event_id, from_status_id, to_status_id, changed_by)
values ($1, $2, $3, $4);", conn, tx))
                    {
                        hist.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        hist.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = from });
                        hist.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = to });
                        hist.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                        await hist.ExecuteNonQueryAsync(token);
                    }

                    // outbox row: event.status_changed { eventId, fromStatusId, toStatusId, notify }.
                    var payload = JsonSerializer.Serialize(new
                    {
                        eventId = id,
                        fromStatusId = from,
                        toStatusId = to,
                        notify = body.Notify,
                    });
                    await using (var outbox = new NpgsqlCommand(@"
insert into outbox (topic, payload) values ('event.status_changed', $1::jsonb);", conn, tx))
                    {
                        outbox.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = payload });
                        await outbox.ExecuteNonQueryAsync(token);
                    }

                    var dto = await ReadEventByIdAsync(conn, tx, id, options, token);
                    if (dto is null) throw NotFound();
                    return dto;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminEvents")
            .Accepts<ChangeEventStatusRequest>("application/json")
            .Produces<EventDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // GET /admin/events/{id}/status-history → 200 { items } newest first.
    private static void MapStatusHistory(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/events/{id:long}/status-history",
            async (long id, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<StatusHistoryDto>();
                await using var cmd = new NpgsqlCommand(@"
select id, event_id, from_status_id, to_status_id, changed_by, changed_at
from event_status_history
where event_id = $1
order by changed_at desc, id desc;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new StatusHistoryDto
                    {
                        Id = reader.GetInt64(0),
                        EventId = reader.GetInt64(1),
                        FromStatusId = reader.IsDBNull(2) ? null : reader.GetInt16(2),
                        ToStatusId = reader.GetInt16(3),
                        ChangedBy = reader.GetString(4),
                        ChangedAt = reader.GetFieldValue<DateTimeOffset>(5),
                    });
                }
                return Results.Ok(new ItemsResponse<StatusHistoryDto> { Items = items });
            })
            .WithTags("AdminEvents")
            .Produces<ItemsResponse<StatusHistoryDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // GET /admin/events/{id}/messages → newest first.
    private static void MapMessages(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/events/{id:long}/messages",
            async (long id, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<EventMessageDto>();
                await using var cmd = new NpgsqlCommand(@"
select id, event_id, body, event_time, created_by, created_at, updated_at
from event_message
where event_id = $1
order by created_at desc, id desc;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) items.Add(ReadMessage(reader));
                return Results.Ok(new ItemsResponse<EventMessageDto> { Items = items });
            })
            .WithTags("AdminEvents")
            .Produces<ItemsResponse<EventMessageDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/events/{id}/messages [snapshot]. body 1..1000; notify required;
    // outbox row only when notify is true.
    private static void MapCreateMessage(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/events/{id:long}/messages",
            async (long id, CreateEventMessageRequest body, HttpContext ctx, AdminSnapshotTransaction snap, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (string.IsNullOrWhiteSpace(body.Body) || body.Body.Length > 1000)
                    v.Field("body", "must be 1 to 1000 characters");
                v.ThrowIfInvalid();
                var email = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<EventMessageDto>(async (conn, tx, token) =>
                {
                    await using (var check = new NpgsqlCommand("select 1 from event where id = $1;", conn, tx))
                    {
                        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r = await check.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull) throw NotFound();
                    }
                    long msgId;
                    EventMessageDto stored;
                    await using (var ins = new NpgsqlCommand(@"
insert into event_message (event_id, body, event_time, created_by)
values ($1, $2, $3, $4)
returning id, event_id, body, event_time, created_by, created_at, updated_at;", conn, tx))
                    {
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Body.Trim() });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)body.EventTime?.ToUniversalTime() ?? DBNull.Value });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                        await using var reader = await ins.ExecuteReaderAsync(token);
                        await reader.ReadAsync(token);
                        stored = ReadMessage(reader);
                        msgId = stored.Id;
                    }
                    if (body.Notify)
                    {
                        var payload = JsonSerializer.Serialize(new { eventId = id, messageId = msgId });
                        await using var outbox = new NpgsqlCommand(@"
insert into outbox (topic, payload) values ('event.message_posted', $1::jsonb);", conn, tx);
                        outbox.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = payload });
                        await outbox.ExecuteNonQueryAsync(token);
                    }
                    return stored;
                }, ct);
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminEvents")
            .Accepts<CreateEventMessageRequest>("application/json")
            .Produces<EventMessageDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PATCH /admin/events/{id}/messages/{messageId} [snapshot].
    private static void MapPatchMessage(IEndpointRouteBuilder app)
    {
        app.MapPatch("/admin/events/{id:long}/messages/{messageId:long}",
            async (long id, long messageId, PatchEventMessageRequest body, HttpContext ctx, AdminSnapshotTransaction snap, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (body.Body is not null && (body.Body.Length < 1 || body.Body.Length > 1000))
                    v.Field("body", "must be 1 to 1000 characters");
                v.ThrowIfInvalid();
                var email = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<EventMessageDto>(async (conn, tx, token) =>
                {
                    var sets = new List<string>();
                    var parameters = new List<NpgsqlParameter>();
                    var next = 1;
                    if (body.Body is not null)
                    {
                        sets.Add($"body = ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Body.Trim() });
                    }
                    if (body.EventTime is not null)
                    {
                        sets.Add($"event_time = ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = body.EventTime.Value.ToUniversalTime() });
                    }
                    sets.Add("updated_at = now()");
                    var eventIdIdx = next++;
                    var msgIdIdx = next;
                    var sql = @"
update event_message set " + string.Join(", ", sets) + @"
where event_id = $" + eventIdIdx + " and id = $" + msgIdIdx + @"
returning id, event_id, body, event_time, created_by, created_at, updated_at;";
                    await using var upd = new NpgsqlCommand(sql, conn, tx);
                    foreach (var p in parameters) upd.Parameters.Add(p);
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = messageId });
                    await using var reader = await upd.ExecuteReaderAsync(token);
                    if (!await reader.ReadAsync(token)) throw NotFound();
                    _ = email;
                    return ReadMessage(reader);
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminEvents")
            .Accepts<PatchEventMessageRequest>("application/json")
            .Produces<EventMessageDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // DELETE /admin/events/{id}/messages/{messageId} [snapshot].
    private static void MapDeleteMessage(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/events/{id:long}/messages/{messageId:long}",
            async (long id, long messageId, HttpContext ctx, AdminSnapshotTransaction snap, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await snap.RunAsync<object?>(async (conn, tx, token) =>
                {
                    await using var del = new NpgsqlCommand(
                        "delete from event_message where event_id = $1 and id = $2;", conn, tx);
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = messageId });
                    var rows = await del.ExecuteNonQueryAsync(token);
                    if (rows == 0) throw NotFound();
                    return null;
                }, ct);
                return Results.NoContent();
            })
            .WithTags("AdminEvents")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // GET /admin/events/{id}/locations?cursor=&limit=&beaconId=&publishedOnly=false
    // Ordered by seq asc; keyset paging on seq; with Accept: text/csv streams
    // every matching row (paging ignored). Header:
    //   seq,beaconId,published,recordedAt,receivedAt,lat,lng,speedMps,altitudeM,headingDeg,accuracyM
    private static void MapLocations(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/events/{id:long}/locations",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var query = ctx.Request.Query;
                var cursorText = query["cursor"].ToString();
                var limitText = query["limit"].ToString();
                var beaconIdText = query["beaconId"].ToString();
                var publishedOnlyText = query["publishedOnly"].ToString();

                long? cursor = AdminHelpers.DecodeLongCursor(string.IsNullOrEmpty(cursorText) ? null : cursorText);
                int? limitRaw = null;
                if (!string.IsNullOrEmpty(limitText))
                {
                    if (!int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLimit))
                        throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "limit malformed");
                    limitRaw = parsedLimit;
                }
                long? beaconIdFilter = null;
                if (!string.IsNullOrEmpty(beaconIdText))
                {
                    if (!long.TryParse(beaconIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBeaconId))
                        throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "beaconId malformed");
                    beaconIdFilter = parsedBeaconId;
                }
                bool publishedOnly = string.Equals(publishedOnlyText, "true", StringComparison.OrdinalIgnoreCase);
                bool csv = ctx.Request.Headers.Accept.ToString().Contains("text/csv", StringComparison.OrdinalIgnoreCase);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                // Verify the event exists.
                await using (var check = new NpgsqlCommand("select 1 from event where id = $1;", conn))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound();
                }

                if (csv) return StreamCsvResult(id, beaconIdFilter, publishedOnly, connections);

                var limit = AdminHelpers.ClampLimit(limitRaw, defaultLimit: 100, max: 500);
                var sql = BuildLocationsSql(cursor is not null, beaconIdFilter is not null, publishedOnly);
                var items = new List<LocationRowDto>();
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                if (cursor is not null)
                    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cursor.Value });
                if (beaconIdFilter is not null)
                    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconIdFilter.Value });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit + 1 });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new LocationRowDto
                    {
                        Seq = reader.GetInt64(0),
                        BeaconId = reader.GetInt64(1),
                        Published = reader.GetBoolean(2),
                        RecordedAt = reader.GetFieldValue<DateTimeOffset>(3),
                        ReceivedAt = reader.GetFieldValue<DateTimeOffset>(4),
                        Lat = reader.GetDouble(5),
                        Lng = reader.GetDouble(6),
                        SpeedMps = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                        AltitudeM = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                        HeadingDeg = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                        AccuracyM = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    });
                }
                string? nextCursor = null;
                if (items.Count > limit)
                {
                    var last = items[limit - 1];
                    nextCursor = AdminHelpers.EncodeLongCursor(last.Seq);
                    items.RemoveRange(limit, items.Count - limit);
                }
                return Results.Ok(new PageResponse<LocationRowDto> { Items = items, NextCursor = nextCursor });
            })
            .WithTags("AdminEvents")
            .Produces<PageResponse<LocationRowDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static string BuildLocationsSql(bool hasCursor, bool hasBeacon, bool publishedOnly)
    {
        var conditions = new List<string> { "event_id = $1" };
        var next = 2;
        if (hasCursor)
            conditions.Add($"seq > ${next++}");
        if (hasBeacon)
            conditions.Add($"beacon_id = ${next++}");
        if (publishedOnly)
            conditions.Add("published");
        var limitIdx = next;
        return @"
select seq, beacon_id, published, recorded_at, received_at, lat, lng, speed_mps, altitude_m, heading_deg, accuracy_m
from location
where " + string.Join(" and ", conditions) + @"
order by seq
limit $" + limitIdx + ";";
    }

    // CSV: stream every matching row with Transfer-Encoding: chunked. Header
    // matches contracts 4.5. Paging ignored.
    private static IResult StreamCsvResult(long eventId, long? beaconIdFilter, bool publishedOnly, WmsfoConnectionStrings connections)
    {
        return new CsvLocationsResult(eventId, beaconIdFilter, publishedOnly, connections);
    }

    private sealed class CsvLocationsResult : IResult
    {
        private readonly long _eventId;
        private readonly long? _beaconId;
        private readonly bool _publishedOnly;
        private readonly WmsfoConnectionStrings _connections;

        public CsvLocationsResult(long eventId, long? beaconId, bool publishedOnly, WmsfoConnectionStrings connections)
        {
            _eventId = eventId;
            _beaconId = beaconId;
            _publishedOnly = publishedOnly;
            _connections = connections;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "text/csv; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "no-store";
            var ct = httpContext.RequestAborted;

            await httpContext.Response.WriteAsync(
                "seq,beaconId,published,recordedAt,receivedAt,lat,lng,speedMps,altitudeM,headingDeg,accuracyM\n", ct);

            await using var conn = new NpgsqlConnection(_connections.App);
            await conn.OpenAsync(ct);

            long? lastSeq = null;
            const int batchSize = 5000;
            while (true)
            {
                var conditions = new List<string> { "event_id = $1" };
                var next = 2;
                if (lastSeq is not null) conditions.Add($"seq > ${next++}");
                if (_beaconId is not null) conditions.Add($"beacon_id = ${next++}");
                if (_publishedOnly) conditions.Add("published");
                var limitIdx = next;
                var sql = @"
select seq, beacon_id, published, recorded_at, received_at, lat, lng, speed_mps, altitude_m, heading_deg, accuracy_m
from location
where " + string.Join(" and ", conditions) + @"
order by seq
limit $" + limitIdx + ";";
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = _eventId });
                if (lastSeq is not null) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = lastSeq.Value });
                if (_beaconId is not null) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = _beaconId.Value });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = batchSize });

                var rowsInBatch = 0;
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var seq = reader.GetInt64(0);
                        var beaconId = reader.GetInt64(1);
                        var published = reader.GetBoolean(2);
                        var recordedAt = reader.GetFieldValue<DateTimeOffset>(3);
                        var receivedAt = reader.GetFieldValue<DateTimeOffset>(4);
                        var lat = reader.GetDouble(5);
                        var lng = reader.GetDouble(6);
                        double? speedMps = reader.IsDBNull(7) ? null : reader.GetDouble(7);
                        double? altitudeM = reader.IsDBNull(8) ? null : reader.GetDouble(8);
                        double? headingDeg = reader.IsDBNull(9) ? null : reader.GetDouble(9);
                        double? accuracyM = reader.IsDBNull(10) ? null : reader.GetDouble(10);
                        var line = string.Create(CultureInfo.InvariantCulture, $"{seq},{beaconId},{(published ? "true" : "false")},{FormatTs(recordedAt)},{FormatTs(receivedAt)},{Fmt(lat)},{Fmt(lng)},{FmtOpt(speedMps)},{FmtOpt(altitudeM)},{FmtOpt(headingDeg)},{FmtOpt(accuracyM)}\n");
                        await httpContext.Response.WriteAsync(line, ct);
                        lastSeq = seq;
                        rowsInBatch++;
                    }
                }
                await httpContext.Response.Body.FlushAsync(ct);
                if (rowsInBatch < batchSize) break;
            }
        }

        private static string FormatTs(DateTimeOffset ts) =>
            ts.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        private static string Fmt(double d) => d.ToString("R", CultureInfo.InvariantCulture);
        private static string FmtOpt(double? d) => d is null ? "" : d.Value.ToString("R", CultureInfo.InvariantCulture);
    }

    // GET /admin/events/{id}/cookies?cursor=&limit=&includeHidden=true
    // Newest first (id desc keyset). includeHidden defaults to true.
    private static void MapCookies(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/events/{id:long}/cookies",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var query = ctx.Request.Query;
                var cursorText = query["cursor"].ToString();
                var limitText = query["limit"].ToString();
                var includeHiddenText = query["includeHidden"].ToString();

                long? cursor = AdminHelpers.DecodeLongCursor(string.IsNullOrEmpty(cursorText) ? null : cursorText);
                int? limitRaw = null;
                if (!string.IsNullOrEmpty(limitText))
                {
                    if (!int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "limit malformed");
                    limitRaw = parsed;
                }
                bool includeHidden = string.IsNullOrEmpty(includeHiddenText) || string.Equals(includeHiddenText, "true", StringComparison.OrdinalIgnoreCase);
                var limit = AdminHelpers.ClampLimit(limitRaw, 100, 500);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                var conditions = new List<string> { "c.event_id = $1" };
                var next = 2;
                if (cursor is not null) conditions.Add($"c.id < ${next++}");
                if (!includeHidden) conditions.Add("c.hidden_at is null");
                var limitIdx = next;
                var sql = @"
select c.id, c.event_id, c.person_id, coalesce(p.email, ''), c.cookie_type_id, c.note, c.left_at, c.hidden_at, c.hidden_by
from cookie c
left join person p on p.id = c.person_id
where " + string.Join(" and ", conditions) + @"
order by c.id desc
limit $" + limitIdx + ";";
                var items = new List<CookieAdminDto>();
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                if (cursor is not null) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = cursor.Value });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit + 1 });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new CookieAdminDto
                    {
                        Id = reader.GetInt64(0),
                        EventId = reader.GetInt64(1),
                        PersonId = reader.GetInt64(2),
                        PersonEmail = reader.GetString(3),
                        CookieTypeId = reader.GetInt64(4),
                        Note = reader.IsDBNull(5) ? null : reader.GetString(5),
                        LeftAt = reader.GetFieldValue<DateTimeOffset>(6),
                        HiddenAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                        HiddenBy = reader.IsDBNull(8) ? null : reader.GetString(8),
                    });
                }
                string? nextCursor = null;
                if (items.Count > limit)
                {
                    nextCursor = AdminHelpers.EncodeLongCursor(items[limit - 1].Id);
                    items.RemoveRange(limit, items.Count - limit);
                }
                return Results.Ok(new PageResponse<CookieAdminDto> { Items = items, NextCursor = nextCursor });
            })
            .WithTags("AdminEvents")
            .Produces<PageResponse<CookieAdminDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- helpers ---

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "not found");

    // Base select for Event responses (contracts 4.5 Events). Includes the
    // linked route (for routeUrl) and the linked route image media asset (for
    // routeImage). Column order used by ReadEvent.
    private const string EventSelectSql = @"
select e.id, e.year, e.name, e.status_id, e.is_current, e.scheduled_at, e.went_live_at, e.ended_at,
       e.funds_percent, e.route_id, r.url, e.route_image_media_id, e.created_by, e.created_at, e.updated_at,
       m.filename, m.content_type, m.kind, m.state, m.s3_key,
       m.size_bytes, m.width, m.height, m.sha256, m.variants,
       m.alt, m.title, m.uploaded_by, m.created_at, m.confirmed_at,
       m.unreferenced_since, m.orphaned_at
from event e
left join route r on r.id = e.route_id
left join media_asset m on m.id = e.route_image_media_id";

    private static async Task<EventDto?> ReadEventByIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, WmsfoOptions options, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(EventSelectSql + " where e.id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadEvent(reader, options);
    }

    private static EventDto ReadEvent(NpgsqlDataReader reader, WmsfoOptions options)
    {
        var dto = new EventDto
        {
            Id = reader.GetInt64(0),
            Year = reader.GetInt32(1),
            Name = reader.GetString(2),
            StatusId = reader.GetInt16(3),
            IsCurrent = reader.GetBoolean(4),
            ScheduledAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            WentLiveAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            EndedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            FundsPercent = reader.GetInt32(8),
            RouteId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            RouteUrl = reader.IsDBNull(10) ? null : reader.GetString(10),
            RouteImageMediaId = reader.IsDBNull(11) ? null : reader.GetGuid(11).ToString(),
            CreatedBy = reader.GetString(12),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(13),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(14),
        };
        if (!reader.IsDBNull(11))
        {
            var cdn = options.CdnBaseUrl.TrimEnd('/');
            var s3Key = reader.GetString(19);
            var variantsJson = reader.IsDBNull(24) ? "{}" : reader.GetString(24);
            var variants = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using (var doc = System.Text.Json.JsonDocument.Parse(variantsJson))
            {
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    var v = entry.Value.GetString() ?? "";
                    variants[entry.Name] = cdn + "/" + v;
                }
            }
            dto.RouteImage = new MediaAssetDto
            {
                Id = dto.RouteImageMediaId ?? "",
                Filename = reader.GetString(15),
                ContentType = reader.GetString(16),
                Kind = reader.GetString(17),
                State = reader.GetString(18),
                SizeBytes = reader.IsDBNull(20) ? null : reader.GetInt64(20),
                Width = reader.IsDBNull(21) ? null : reader.GetInt32(21),
                Height = reader.IsDBNull(22) ? null : reader.GetInt32(22),
                Sha256 = reader.IsDBNull(23) ? null : reader.GetString(23).Trim(),
                Variants = variants,
                Alt = reader.GetString(25),
                Title = reader.GetString(26),
                UploadedBy = reader.GetString(27),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(28),
                ConfirmedAt = reader.IsDBNull(29) ? null : reader.GetFieldValue<DateTimeOffset>(29),
                UnreferencedSince = reader.IsDBNull(30) ? null : reader.GetFieldValue<DateTimeOffset>(30),
                OrphanedAt = reader.IsDBNull(31) ? null : reader.GetFieldValue<DateTimeOffset>(31),
                Url = cdn + "/" + s3Key,
            };
        }
        return dto;
    }

    private static EventMessageDto ReadMessage(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        EventId = reader.GetInt64(1),
        Body = reader.GetString(2),
        EventTime = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
        CreatedBy = reader.GetString(4),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(6),
    };
}
