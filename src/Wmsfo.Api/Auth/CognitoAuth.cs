using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data;
using Wmsfo.Api.Data.Sql;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Auth;

// api.md 6.2: JwtBearer for Cognito ID tokens, one scheme per pool (the people
// pool and the admin pool, contracts 3.1). Issuer, audiences, token_use=id,
// 60 s skew; the scheme stamps the wmsfo_pool claim. The person-upsert filter
// runs after validation and attaches wmsfo_person_id to the request, or answers
// 401 unknown_user when the people pool no longer has the token's user. Policies
// are added by the pipeline extension.
public static class CognitoAuth
{
    public static JwtBearerOptions ConfigurePeople(JwtBearerOptions options, WmsfoOptions cfg) =>
        Configure(options, cfg, cfg.CognitoIssuer, cfg.CognitoClientIdList(), PersonClaims.PoolPeople);

    public static JwtBearerOptions ConfigureAdmin(JwtBearerOptions options, WmsfoOptions cfg) =>
        Configure(options, cfg, cfg.EffectiveAdminIssuer, cfg.EffectiveAdminClientIdList(), PersonClaims.PoolAdmin);

    // The policy scheme's selector: the token's unverified iss picks the pool
    // whose scheme then validates it; anything else goes to the people scheme
    // and fails there.
    public static string SelectScheme(string authorizationHeader, WmsfoOptions cfg)
    {
        var iss = JwtIssuerPeek.TryRead(authorizationHeader);
        return string.Equals(iss, cfg.EffectiveAdminIssuer, StringComparison.Ordinal)
            ? AuthSchemes.CognitoAdminJwt
            : AuthSchemes.CognitoPeopleJwt;
    }

    private static JwtBearerOptions Configure(JwtBearerOptions options, WmsfoOptions cfg, string issuer, IReadOnlyList<string> audiences, string pool)
    {
        options.Authority = issuer;
        options.RequireHttpsMetadata = cfg.Env != "dev";
        options.SaveToken = false;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new()
        {
            ValidIssuer = issuer,
            ValidAudiences = audiences,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(60),
            NameClaimType = "email",
            RoleClaimType = "cognito:groups",
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                var use = ctx.Principal?.FindFirst(PersonClaims.TokenUse)?.Value;
                if (!string.Equals(use, "id", StringComparison.Ordinal))
                {
                    ctx.Fail("token_use must be id");
                    return Task.CompletedTask;
                }
                if (ctx.Principal?.Identity is ClaimsIdentity ident && ident.FindFirst(PersonClaims.Pool) is null)
                {
                    ident.AddClaim(new Claim(PersonClaims.Pool, pool));
                }
                return Task.CompletedTask;
            },
        };
        return options;
    }

    // Endpoint filter that ensures the person row (contracts 3.1) for the ID
    // token's sub + email and attaches the row id to the claims principal. A
    // token whose user the people pool no longer has answers 401 unknown_user
    // and the handler never runs.
    public static async ValueTask<object?> PersonUpsertFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var user = http.User;
        var sub = user.FindFirst(PersonClaims.Sub)?.Value;
        var email = user.FindFirst(PersonClaims.Email)?.Value;
        if (!string.IsNullOrEmpty(sub) && !string.IsNullOrEmpty(email))
        {
            var upsert = http.RequestServices.GetService(typeof(IPersonUpsert)) as IPersonUpsert;
            if (upsert is not null)
            {
                ApplyUpsertResult(await upsert.EnsureAsync(sub, email, http.RequestAborted), user);
            }
        }
        return await next(ctx);
    }

    // Maps the upsert's answer onto the request: Found attaches the person id
    // claim (once), UnknownUser throws 401 unknown_user (contracts 4.7).
    public static void ApplyUpsertResult(PersonUpsertResult result, ClaimsPrincipal user)
    {
        switch (result)
        {
            case PersonUpsertResult.Found found:
                if (user.Identity is ClaimsIdentity ident && ident.FindFirst(PersonClaims.PersonId) is null)
                {
                    ident.AddClaim(new Claim(PersonClaims.PersonId,
                        found.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
                break;
            case PersonUpsertResult.UnknownUser:
                throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.UnknownUser,
                    "the token's user no longer exists; sign in again");
            default:
                throw new InvalidOperationException("unknown person upsert result " + result.GetType().Name);
        }
    }

    public static long? TryGetPersonId(ClaimsPrincipal? principal)
    {
        var claim = principal?.FindFirst(PersonClaims.PersonId)?.Value;
        return long.TryParse(claim, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}

// Reads the iss claim out of a bearer JWT without verifying it; the scheme
// picked by the answer verifies the token in full.
public static class JwtIssuerPeek
{
    public static string? TryRead(string authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader) || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = authorizationHeader.Substring("Bearer ".Length).Trim();
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("iss", out var iss) && iss.ValueKind == System.Text.Json.JsonValueKind.String ? iss.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

// sql.md 8.1 person upsert. Extracted so dev static tokens and integration tests
// can plug in without hitting Postgres.
public interface IPersonUpsert
{
    Task<PersonUpsertResult> EnsureAsync(string sub, string email, CancellationToken ct);
}

// The upsert's answer: the person's row id, or the token's user is not a user
// of the people pool (and nothing was written).
public abstract record PersonUpsertResult
{
    private PersonUpsertResult() { }

    public sealed record Found(long Id) : PersonUpsertResult;

    public sealed record UnknownUser : PersonUpsertResult;

    public static readonly UnknownUser Unknown = new();
}

// Contracts 3.1 and sql.md 8.1: one address is one person. The hot path is one
// update by sub with no Cognito call. No row (first sight of the sub, or an
// email change that collides with person_email_lower_key) asks the people pool
// once: a missing user is UnknownUser with nothing written; an existing one gets
// its row in one transaction after any stale row holding the address is deleted
// with its cascade.
public sealed class DbPersonUpsert : IPersonUpsert
{
    public const string EmailLowerKey = "person_email_lower_key";

    private readonly WmsfoConnectionStrings _connections;
    private readonly IPersonDirectory _directory;
    private readonly ILogger<DbPersonUpsert> _logger;

    public DbPersonUpsert(WmsfoConnectionStrings connections, IPersonDirectory directory, ILogger<DbPersonUpsert> logger)
    {
        _connections = connections;
        _directory = directory;
        _logger = logger;
    }

    public async Task<PersonUpsertResult> EnsureAsync(string sub, string email, CancellationToken ct)
    {
        if (!Guid.TryParse(sub, out var cognitoSub)) return PersonUpsertResult.Unknown;

        var touched = await TouchAsync(cognitoSub, email, ct);
        if (touched is long id) return new PersonUpsertResult.Found(id);

        if (!await _directory.ExistsAsync(sub, ct)) return PersonUpsertResult.Unknown;

        try
        {
            return new PersonUpsertResult.Found(await FirstSightAsync(cognitoSub, email, ct));
        }
        catch (PostgresException ex) when (IsEmailLowerKey(ex))
        {
            // A concurrent first sight wrote the address between the delete and
            // the insert; the second run deletes that row like any stale one.
            return new PersonUpsertResult.Found(await FirstSightAsync(cognitoSub, email, ct));
        }
    }

    private async Task<long?> TouchAsync(Guid cognitoSub, string email, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct);
        await using var command = new NpgsqlCommand(TransactionRecipes.PersonTouch, conn);
        command.Parameters.AddWithValue("sub", cognitoSub);
        command.Parameters.AddWithValue("email", email);
        try
        {
            return ReadId(await command.ExecuteScalarAsync(ct));
        }
        catch (PostgresException ex) when (IsEmailLowerKey(ex))
        {
            return null;
        }
    }

    private async Task<long> FirstSightAsync(Guid cognitoSub, string email, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var superseded = new List<long>();
        await using (var delete = new NpgsqlCommand(TransactionRecipes.PersonSupersede, conn, tx))
        {
            delete.Parameters.AddWithValue("sub", cognitoSub);
            delete.Parameters.AddWithValue("email", email);
            await using var reader = await delete.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) superseded.Add(reader.GetInt64(0));
        }

        long id;
        await using (var insert = new NpgsqlCommand(TransactionRecipes.PersonInsert, conn, tx))
        {
            insert.Parameters.AddWithValue("sub", cognitoSub);
            insert.Parameters.AddWithValue("email", email);
            id = ReadId(await insert.ExecuteScalarAsync(ct))
                ?? throw new InvalidOperationException("person insert returned no id");
        }

        await tx.CommitAsync(ct);
        foreach (var oldId in superseded)
        {
            _logger.LogInformation("person superseded oldPersonId={OldPersonId} newPersonId={NewPersonId}", oldId, id);
        }
        return id;
    }

    private static bool IsEmailLowerKey(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(ex.ConstraintName, EmailLowerKey, StringComparison.Ordinal);

    private static long? ReadId(object? scalar) =>
        scalar is long l ? l : (scalar is int i ? i : null);
}
