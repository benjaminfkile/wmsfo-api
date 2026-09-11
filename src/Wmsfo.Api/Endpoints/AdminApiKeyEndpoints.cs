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
using Wmsfo.Api.Security;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 API keys (Admin, Cognito only). The three endpoints refuse an
// API-key principal (DenyApiKeys) - a key can never mint, list, or revoke keys
// (contracts 3.6). Minting generates the plaintext with Keys.MintApiKey, stores
// sha256(key) and the first 12 characters for display, and returns the key
// exactly once.
public static class AdminApiKeyEndpoints
{
    private const int NameMax = 100;

    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapMint(app);
        MapRevoke(app);
    }

    // GET /admin/api-keys -> 200 { items: ApiKey[] } newest first, revoked and
    // expired included.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/api-keys",
            async (HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<ApiKeyDto>();
                await using var cmd = new NpgsqlCommand(
                    ApiKeySelect + " order by id desc;", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) items.Add(ReadRow(reader));
                return Results.Ok(new ItemsResponse<ApiKeyDto> { Items = items });
            })
            .WithTags("AdminApiKeys")
            .Produces<ItemsResponse<ApiKeyDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .DenyApiKeys()
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/api-keys -> 201 ApiKeyMinted. The key is returned exactly once.
    private static void MapMint(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/api-keys",
            async (CreateApiKeyRequest body, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                var normalized = Validate(body);

                var minted = Keys.MintApiKey();
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                // Uniqueness among unrevoked names is enforced by the partial
                // index; check ahead of time so the error is 409 name_taken
                // rather than a raw pg exception, and re-check inside the
                // transaction to catch the race.
                if (await NameTakenAsync(conn, null, normalized.Name, ct))
                {
                    throw NameTaken();
                }
                long id;
                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    if (await NameTakenAsync(conn, tx, normalized.Name, ct))
                    {
                        throw NameTaken();
                    }
                    try
                    {
                        await using var insert = new NpgsqlCommand(@"
insert into api_key (name, key_prefix, key_hash, all_capabilities, capabilities, expires_at, created_by)
values ($1, $2, $3, $4, $5, $6, $7)
returning id;", conn, tx);
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = normalized.Name });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = minted.Prefix });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = minted.Hash });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = normalized.AllCapabilities });
                        insert.Parameters.Add(new NpgsqlParameter
                        {
                            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                            Value = normalized.Capabilities,
                        });
                        insert.Parameters.Add(new NpgsqlParameter
                        {
                            NpgsqlDbType = NpgsqlDbType.TimestampTz,
                            Value = (object?)normalized.ExpiresAt ?? DBNull.Value,
                        });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                        id = (long)(await insert.ExecuteScalarAsync(ct) ?? 0L);
                    }
                    catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == "api_key_name_ux")
                    {
                        throw NameTaken();
                    }
                    await tx.CommitAsync(ct);
                }

                var dto = await ReadByIdAsync(conn, null, id, ct)
                    ?? throw new ApiException(StatusCodes.Status500InternalServerError,
                        ApiErrorCodes.InternalError, "api key vanished after insert");

                return Results.Json(new ApiKeyMintedDto
                {
                    Id = dto.Id,
                    Name = dto.Name,
                    KeyPrefix = dto.KeyPrefix,
                    AllCapabilities = dto.AllCapabilities,
                    Capabilities = dto.Capabilities,
                    ExpiresAt = dto.ExpiresAt,
                    CreatedBy = dto.CreatedBy,
                    CreatedAt = dto.CreatedAt,
                    LastUsedAt = dto.LastUsedAt,
                    RevokedAt = dto.RevokedAt,
                    Key = minted.Token,
                }, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminApiKeys")
            .Accepts<CreateApiKeyRequest>("application/json")
            .Produces<ApiKeyMintedDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .DenyApiKeys()
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/api-keys/{id}/revoke -> 200 ApiKey (revokedAt set; idempotent).
    private static void MapRevoke(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/api-keys/{id:long}/revoke",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                int rows;
                await using (var upd = new NpgsqlCommand(
                    "update api_key set revoked_at = coalesce(revoked_at, now()) where id = $1;", conn))
                {
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    rows = await upd.ExecuteNonQueryAsync(ct);
                }
                if (rows == 0)
                {
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "api key not found");
                }
                var dto = await ReadByIdAsync(conn, null, id, ct)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "api key not found");
                return Results.Ok(dto);
            })
            .WithTags("AdminApiKeys")
            .Produces<ApiKeyDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .DenyApiKeys()
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // Validation: name 1..100, unique among unrevoked (409 name_taken is checked
    // in Mint). allCapabilities required; capabilities required and either empty
    // with allCapabilities=true, or a non-empty distinct subset of the known list.
    // expiresAt null or at least one hour ahead.
    private static NormalizedMintRequest Validate(CreateApiKeyRequest body)
    {
        var v = new RequestValidation();
        var name = (body.Name ?? "").Trim();
        if (string.IsNullOrEmpty(name) || name.Length > NameMax)
        {
            v.Field("name", $"must be 1 to {NameMax} characters");
        }
        if (body.AllCapabilities is null)
        {
            v.Field("allCapabilities", "required");
        }
        if (body.Capabilities is null)
        {
            v.Field("capabilities", "required");
        }
        var caps = body.Capabilities ?? new List<string>();
        var allCaps = body.AllCapabilities ?? false;
        if (body.Capabilities is not null)
        {
            if (allCaps && caps.Count > 0)
            {
                v.Field("capabilities", "must be empty when allCapabilities is true");
            }
            if (!allCaps && caps.Count == 0)
            {
                v.Field("capabilities", "must be a non-empty subset");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cap in caps)
            {
                if (cap is null || !ApiKeyCapabilities.IsKnown(cap))
                {
                    v.Field("capabilities", "must contain only known capability names");
                    break;
                }
                if (!seen.Add(cap))
                {
                    v.Field("capabilities", "must be distinct");
                    break;
                }
            }
        }
        if (body.ExpiresAt is { } expires && expires < DateTimeOffset.UtcNow.AddHours(1))
        {
            v.Field("expiresAt", "must be null or at least one hour ahead");
        }
        v.ThrowIfInvalid();
        return new NormalizedMintRequest(
            Name: name,
            AllCapabilities: allCaps,
            Capabilities: allCaps ? Array.Empty<string>() : caps.ToArray(),
            ExpiresAt: body.ExpiresAt);
    }

    private sealed record NormalizedMintRequest(string Name, bool AllCapabilities, string[] Capabilities, DateTimeOffset? ExpiresAt);

    private static async Task<bool> NameTakenAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string name, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select 1 from api_key where name = $1 and revoked_at is null limit 1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is not null && r is not DBNull;
    }

    private const string ApiKeySelect = @"
select id, name, key_prefix, all_capabilities, capabilities, expires_at,
       created_by, created_at, last_used_at, revoked_at
from api_key";

    private static async Task<ApiKeyDto?> ReadByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(ApiKeySelect + " where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadRow(reader);
    }

    private static ApiKeyDto ReadRow(NpgsqlDataReader r)
    {
        var caps = r.IsDBNull(4) ? Array.Empty<string>() : r.GetFieldValue<string[]>(4);
        return new ApiKeyDto
        {
            Id = r.GetInt64(0),
            Name = r.GetString(1),
            KeyPrefix = r.GetString(2),
            AllCapabilities = r.GetBoolean(3),
            Capabilities = caps.ToList(),
            ExpiresAt = r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
            CreatedBy = r.GetString(6),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(7),
            LastUsedAt = r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
            RevokedAt = r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
        };
    }

    private static ApiException NameTaken() =>
        new(StatusCodes.Status409Conflict, "name_taken", "an unrevoked key already uses that name");
}
