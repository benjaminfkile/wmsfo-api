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
using Wmsfo.Api.Node;
using Wmsfo.Api.Realtime;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 Beacons (Admin) endpoints. Every /admin/beacons* route is
// under the Admin policy (api.md 6.2, 7). Reads run plain selects; writes go
// through sql.md 8.11 exactly (create, rotate, revoke, activate, deactivate).
// The list and get responses embed `hubConnected` resolved from one presence
// call to the gateway (api.md 12.4 / contracts 1.11).
public static class AdminBeaconEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapGet(app);
        MapCreate(app);
        MapPatch(app);
        MapActivate(app);
        MapDeactivate(app);
        MapRotate(app);
        MapRevoke(app);
        MapLogsList(app);
        MapLogGet(app);
    }

    // GET /admin/beacons → 200 { items: Beacon[], staleAfterS }.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/beacons",
            async (WmsfoConnectionStrings connections, NodeStateService state, IGatewayInternalClient gateway, WmsfoOptions options, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var rows = new List<(BeaconDto Dto, int KeyVersion)>();
                await using (var cmd = new NpgsqlCommand(BeaconSelect + @"
order by name asc, id asc;", conn))
                {
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) rows.Add(ReadBeaconRow(reader));
                }

                var identities = await gateway.GetPresenceAsync(
                    $"{options.ServiceName}:ingest", ct);
                var presence = identities is null ? null : ToPresenceSet(identities);
                foreach (var (dto, keyVersion) in rows)
                {
                    dto.HubConnected = ResolveHubConnected(dto.Id, keyVersion, presence);
                }

                var staleAfterS = state.Current.Settings.BeaconStaleAfterS;
                return Results.Ok(new BeaconsListResponse
                {
                    Items = rows.Select(r => r.Dto).ToList(),
                    StaleAfterS = staleAfterS,
                });
            })
            .WithTags("AdminBeacons")
            .Produces<BeaconsListResponse>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // GET /admin/beacons/{id} → 200 Beacon.
    private static void MapGet(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/beacons/{id:long}",
            async (long id, WmsfoConnectionStrings connections, IGatewayInternalClient gateway, WmsfoOptions options, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var row = await ReadBeaconRowByIdAsync(conn, null, id, ct);
                if (row is null) throw NotFound();

                var identities = await gateway.GetPresenceAsync(
                    $"{options.ServiceName}:ingest", ct);
                var presence = identities is null ? null : ToPresenceSet(identities);
                row.Value.Dto.HubConnected = ResolveHubConnected(row.Value.Dto.Id, row.Value.KeyVersion, presence);
                return Results.Ok(row.Value.Dto);
            })
            .WithTags("AdminBeacons")
            .Produces<BeaconDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/beacons — sql.md 8.11 create. Mints the key, inserts the
    // beacon row, mints the enrollment token and inserts it in the same
    // transaction. The plaintext key appears once in the response and in no log.
    private static void MapCreate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/beacons",
            async (CreateBeaconRequest body, HttpContext ctx, WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 100)
                    v.Field("name", "must be 1 to 100 characters");
                if (body.Notes is null || body.Notes.Length > 2000)
                    v.Field("notes", "must be 0 to 2000 characters");
                if (body.Role != "beacon" && body.Role != "admin")
                    v.Field("role", "must be beacon or admin");
                v.ThrowIfInvalid();
                var email = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, key, enrollment) = await CreateOrRotateAsync(
                    connections, options,
                    createNewBeacon: true,
                    beaconId: null,
                    name: body.Name.Trim(),
                    notes: body.Notes,
                    role: body.Role,
                    createdBy: email,
                    ct);

                return Results.Json(new BeaconWithKeyResponse
                {
                    Beacon = dto,
                    Key = key,
                    Enrollment = enrollment,
                }, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminBeacons")
            .Accepts<CreateBeaconRequest>("application/json")
            .Produces<BeaconWithKeyResponse>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PATCH /admin/beacons/{id} — name and/or notes. Role and key are not
    // modifiable here.
    private static void MapPatch(IEndpointRouteBuilder app)
    {
        app.MapPatch("/admin/beacons/{id:long}",
            async (long id, PatchBeaconRequest body, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (body.Name is not null && (body.Name.Length < 1 || body.Name.Length > 100))
                    v.Field("name", "must be 1 to 100 characters");
                if (body.Notes is not null && body.Notes.Length > 2000)
                    v.Field("notes", "must be 0 to 2000 characters");
                v.ThrowIfInvalid();
                _ = AdminHelpers.RequireAdminEmail(ctx);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                var sets = new List<string>();
                var parameters = new List<NpgsqlParameter>();
                var next = 1;
                if (body.Name is not null)
                {
                    sets.Add($"name = ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Name.Trim() });
                }
                if (body.Notes is not null)
                {
                    sets.Add($"notes = ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Notes });
                }
                if (sets.Count == 0)
                {
                    var dto = await ReadBeaconByIdAsync(conn, null, id, ct);
                    if (dto is null) throw NotFound();
                    return Results.Ok(dto);
                }
                sets.Add("updated_at = now()");
                var whereIdx = next;
                var sql = $"update beacon set {string.Join(", ", sets)} where id = ${whereIdx};";
                await using (var upd = new NpgsqlCommand(sql, conn))
                {
                    foreach (var p in parameters) upd.Parameters.Add(p);
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var rows = await upd.ExecuteNonQueryAsync(ct);
                    if (rows == 0) throw NotFound();
                }
                var updated = await ReadBeaconByIdAsync(conn, null, id, ct);
                if (updated is null) throw NotFound();
                return Results.Ok(updated);
            })
            .WithTags("AdminBeacons")
            .Accepts<PatchBeaconRequest>("application/json")
            .Produces<BeaconDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/beacons/{id}/activate — sql.md 8.11 activate.
    // Two statements in one transaction (clear the previous active, set this
    // one), so beacon_one_active is checked per statement. A concurrent race
    // that collides on the partial unique index is retried once (sql.md 4.3),
    // then bubbles up as 500 if the retry fails too.
    private static void MapActivate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/beacons/{id:long}/activate",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                var dto = await RunActivateAsync(connections, id, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminBeacons")
            .Produces<BeaconDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static async Task<BeaconDto> RunActivateAsync(WmsfoConnectionStrings connections, long id, CancellationToken ct)
    {
        // sql.md 4.3: on 23505 on beacon_one_active retry the transaction once.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await RunActivateOnceAsync(connections, id, ct);
            }
            catch (PostgresException ex)
                when (ex.SqlState == "23505" && ex.ConstraintName == ConstraintErrorMapping.BeaconOneActive && attempt < 1)
            {
                // Retry once; the loser of the race sees the winner's clear on
                // the next attempt.
            }
        }
    }

    private static async Task<BeaconDto> RunActivateOnceAsync(WmsfoConnectionStrings connections, long id, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connections.App);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        DateTimeOffset? revokedAt = null;
        bool found;
        await using (var read = new NpgsqlCommand(
            "select revoked_at from beacon where id = $1 for update;", conn, tx))
        {
            read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await read.ExecuteReaderAsync(ct);
            found = await reader.ReadAsync(ct);
            if (found)
            {
                revokedAt = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
            }
        }
        if (!found) throw NotFound();
        if (revokedAt is not null)
            throw new ApiException(StatusCodes.Status409Conflict, "beacon_revoked", "beacon is revoked");

        // Clear the previous active (if any), then set this one — sql.md 8.11.
        // The two statements are what the partial unique index expects.
        await using (var clr = new NpgsqlCommand(
            "update beacon set is_active = false, updated_at = now() where is_active and id <> $1;", conn, tx))
        {
            clr.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await clr.ExecuteNonQueryAsync(ct);
        }
        await using (var set = new NpgsqlCommand(
            "update beacon set is_active = true, updated_at = now() where id = $1;", conn, tx))
        {
            set.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await set.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        var dto = await ReadBeaconByIdAsync(conn, null, id, ct);
        if (dto is null) throw NotFound();
        return dto;
    }

    // POST /admin/beacons/{id}/deactivate — sql.md 8.11 deactivate. Idempotent.
    private static void MapDeactivate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/beacons/{id:long}/deactivate",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using (var check = new NpgsqlCommand("select 1 from beacon where id = $1;", conn))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound();
                }
                await using (var upd = new NpgsqlCommand(
                    "update beacon set is_active = false, updated_at = now() where id = $1;", conn))
                {
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await upd.ExecuteNonQueryAsync(ct);
                }
                var dto = await ReadBeaconByIdAsync(conn, null, id, ct);
                if (dto is null) throw NotFound();
                return Results.Ok(dto);
            })
            .WithTags("AdminBeacons")
            .Produces<BeaconDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/beacons/{id}/rotate — sql.md 8.11 rotate. Mints a new key,
    // updates key_hash and key_prefix, increments key_version, deletes pending
    // enrollment tokens, inserts a fresh enrollment token in the same
    // transaction. The old key stops working immediately: the REST door reads
    // through the hash (which no longer matches) and the message path re-checks
    // key_version. 409 beacon_revoked on a revoked beacon.
    private static void MapRotate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/beacons/{id:long}/rotate",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                var (dto, key, enrollment) = await CreateOrRotateAsync(
                    connections, options,
                    createNewBeacon: false,
                    beaconId: id,
                    name: null,
                    notes: null,
                    role: null,
                    createdBy: email,
                    ct);
                return Results.Ok(new BeaconWithKeyResponse
                {
                    Beacon = dto,
                    Key = key,
                    Enrollment = enrollment,
                });
            })
            .WithTags("AdminBeacons")
            .Produces<BeaconWithKeyResponse>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/beacons/{id}/revoke — sql.md 8.11 revoke. Sets revoked_at
    // (coalesced so a repeat call is idempotent), clears is_active, deletes
    // pending enrollment tokens. The row itself stays forever.
    private static void MapRevoke(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/beacons/{id:long}/revoke",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                int rows;
                await using (var upd = new NpgsqlCommand(@"
update beacon
set revoked_at = coalesce(revoked_at, now()),
    is_active = false,
    updated_at = now()
where id = $1;", conn, tx))
                {
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    rows = await upd.ExecuteNonQueryAsync(ct);
                }
                if (rows == 0) throw NotFound();
                await using (var del = new NpgsqlCommand(
                    "delete from beacon_enrollment_token where beacon_id = $1 and consumed_at is null;", conn, tx))
                {
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await del.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);

                var dto = await ReadBeaconByIdAsync(conn, null, id, ct);
                if (dto is null) throw NotFound();
                return Results.Ok(dto);
            })
            .WithTags("AdminBeacons")
            .Produces<BeaconDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // GET /admin/beacons/{id}/logs → 200 { items } newest first.
    private static void MapLogsList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/beacons/{id:long}/logs",
            async (long id, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using (var check = new NpgsqlCommand("select 1 from beacon where id = $1;", conn))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound();
                }
                var items = new List<BeaconLogDto>();
                await using var cmd = new NpgsqlCommand(@"
select id, received_at, app_version, size_bytes
from beacon_log
where beacon_id = $1
order by received_at desc, id desc;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new BeaconLogDto
                    {
                        Id = reader.GetInt64(0),
                        ReceivedAt = reader.GetFieldValue<DateTimeOffset>(1),
                        AppVersion = reader.IsDBNull(2) ? null : reader.GetString(2),
                        SizeBytes = reader.GetInt32(3),
                    });
                }
                return Results.Ok(new ItemsResponse<BeaconLogDto> { Items = items });
            })
            .WithTags("AdminBeacons")
            .Produces<ItemsResponse<BeaconLogDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // GET /admin/beacons/{id}/logs/{logId} → 200 text/plain body.
    private static void MapLogGet(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/beacons/{id:long}/logs/{logId:long}",
            async (long id, long logId, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    "select body from beacon_log where beacon_id = $1 and id = $2;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = logId });
                var r = await cmd.ExecuteScalarAsync(ct);
                if (r is null || r is DBNull) throw NotFound();
                return Results.Text((string)r, "text/plain; charset=utf-8");
            })
            .WithTags("AdminBeacons")
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // Shared plumbing for create and rotate: mints the beacon key, mints the
    // enrollment token, writes both in one transaction per sql.md 8.11, then
    // builds the Enrollment envelope with the QR PNG. The plaintext key is
    // returned to the caller and never logged.
    private static async Task<(BeaconDto Beacon, string Key, EnrollmentDto Enrollment)> CreateOrRotateAsync(
        WmsfoConnectionStrings connections,
        WmsfoOptions options,
        bool createNewBeacon,
        long? beaconId,
        string? name,
        string? notes,
        string? role,
        string createdBy,
        CancellationToken ct)
    {
        var minted = Keys.MintKey();
        var enrollment = Keys.MintEnrollmentToken();
        var keyBytes = Convert.FromBase64String(options.EnrollmentEncryptionKey);
        var cipher = Keys.Encrypt(keyBytes, minted.Token);

        long id;
        DateTimeOffset expiresAt;

        await using var conn = new NpgsqlConnection(connections.App);
        await conn.OpenAsync(ct);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            if (createNewBeacon)
            {
                await using var insert = new NpgsqlCommand(@"
insert into beacon (name, notes, role, key_hash, key_prefix, created_by, updated_at)
values ($1, $2, $3, $4, $5, $6, now())
returning id;", conn, tx);
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name ?? "" });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = notes ?? "" });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = role ?? "beacon" });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = minted.Hash });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = minted.Prefix });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = createdBy });
                id = (long)(await insert.ExecuteScalarAsync(ct) ?? 0L);
            }
            else
            {
                id = beaconId ?? throw new InvalidOperationException("beaconId required for rotate");

                DateTimeOffset? revokedAt = null;
                bool found;
                await using (var read = new NpgsqlCommand(
                    "select revoked_at from beacon where id = $1 for update;", conn, tx))
                {
                    read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    found = await reader.ReadAsync(ct);
                    if (found)
                    {
                        revokedAt = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
                    }
                }
                if (!found) throw NotFound();
                if (revokedAt is not null)
                    throw new ApiException(StatusCodes.Status409Conflict, "beacon_revoked", "beacon is revoked");

                await using var upd = new NpgsqlCommand(@"
update beacon
set key_hash = $1,
    key_prefix = $2,
    key_version = key_version + 1,
    updated_at = now()
where id = $3;", conn, tx);
                upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = minted.Hash });
                upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = minted.Prefix });
                upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                await upd.ExecuteNonQueryAsync(ct);
            }

            // Delete any pending enrollment tokens for this beacon (rotate;
            // idempotent for create since none exist yet).
            await using (var delTokens = new NpgsqlCommand(
                "delete from beacon_enrollment_token where beacon_id = $1 and consumed_at is null;", conn, tx))
            {
                delTokens.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                await delTokens.ExecuteNonQueryAsync(ct);
            }

            await using (var insertToken = new NpgsqlCommand(@"
insert into beacon_enrollment_token (beacon_id, token_hash, key_ciphertext, expires_at)
values ($1, $2, $3, now() + interval '15 minutes')
returning expires_at;", conn, tx))
            {
                insertToken.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                insertToken.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = enrollment.Hash });
                insertToken.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = cipher });
                var scalar = await insertToken.ExecuteScalarAsync(ct);
                expiresAt = ((DateTime)scalar!).ToUniversalTime();
            }

            await tx.CommitAsync(ct);
        }

        var dto = await ReadBeaconByIdAsync(conn, null, id, ct);
        if (dto is null) throw NotFound();

        var url = "rednose://enroll?api=" + Uri.EscapeDataString(options.PublicApiBaseUrl)
            + "&token=" + enrollment.Token;
        var qrPngDataUrl = QrRenderer.RenderPngDataUrl(url);

        return (dto, minted.Token, new EnrollmentDto
        {
            Token = enrollment.Token,
            Url = url,
            QrPngDataUrl = qrPngDataUrl,
            ExpiresAt = expiresAt,
        });
    }

    // --- helpers ---

    private const string BeaconSelect = @"
select id, name, notes, role, key_prefix, key_version, is_active, revoked_at,
       last_seen_at, last_location_at, last_heartbeat_at, stale_since,
       telemetry, created_by, created_at, updated_at
from beacon";

    // Read the row without the key_version — used by writes that only need
    // the shape they will respond with, once no hubConnected resolution is
    // needed (create, rotate, patch, activate, deactivate, revoke).
    private static async Task<BeaconDto?> ReadBeaconByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var row = await ReadBeaconRowByIdAsync(conn, tx, id, ct);
        return row?.Dto;
    }

    private static async Task<(BeaconDto Dto, int KeyVersion)?> ReadBeaconRowByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(BeaconSelect + " where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadBeaconRow(reader);
    }

    private static (BeaconDto Dto, int KeyVersion) ReadBeaconRow(NpgsqlDataReader reader)
    {
        HeartbeatBody? telemetry = null;
        if (!reader.IsDBNull(12))
        {
            var raw = reader.GetString(12);
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    telemetry = JsonSerializer.Deserialize<HeartbeatBody>(raw, Wmsfo.Api.Objects.CanonicalJson.Options);
                }
                catch (JsonException)
                {
                    telemetry = null;
                }
            }
        }
        var dto = new BeaconDto
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Notes = reader.GetString(2),
            Role = reader.GetString(3),
            KeyPrefix = reader.GetString(4),
            IsActive = reader.GetBoolean(6),
            RevokedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            LastSeenAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            LastLocationAt = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            LastHeartbeatAt = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            StaleSince = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            Telemetry = telemetry,
            CreatedBy = reader.GetString(13),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(14),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(15),
            HubConnected = null,
        };
        var keyVersion = reader.GetInt32(5);
        return (dto, keyVersion);
    }

    private static HashSet<string> ToPresenceSet(IReadOnlyList<string> identities)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in identities) set.Add(id);
        return set;
    }

    // contracts 1.11: hubConnected is true when the gateway's presence set
    // contains "<id>:<keyVersion>" for this beacon; false when it does not;
    // null when the presence call failed (indicated by a null set).
    private static bool? ResolveHubConnected(long beaconId, int keyVersion, HashSet<string>? presence)
    {
        if (presence is null) return null;
        return presence.Contains(
            $"{beaconId.ToString(CultureInfo.InvariantCulture)}:{keyVersion.ToString(CultureInfo.InvariantCulture)}");
    }

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "not found");
}
