using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Auth;

// api.md 6.2: JwtBearer for Cognito ID tokens, one scheme per pool (the people
// pool and the admin pool, contracts 3.1). Issuer, audiences, token_use=id,
// 60 s skew; the scheme stamps the wmsfo_pool claim. The person-upsert filter
// runs after validation and attaches wmsfo_person_id to the request. Policies
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

    // Endpoint filter that upserts the person row (contracts 3.1) on the ID
    // token's sub + email and attaches the row id to the claims principal.
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
                var id = await upsert.EnsureAsync(sub, email, http.RequestAborted);
                if (id.HasValue && user.Identity is ClaimsIdentity ident && ident.FindFirst(PersonClaims.PersonId) is null)
                {
                    ident.AddClaim(new Claim(PersonClaims.PersonId,
                        id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
        }
        return await next(ctx);
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
    Task<long?> EnsureAsync(string sub, string email, CancellationToken ct);
}

public sealed class DbPersonUpsert : IPersonUpsert
{
    private readonly WmsfoConnectionStrings _connections;

    public DbPersonUpsert(WmsfoConnectionStrings connections)
    {
        _connections = connections;
    }

    public async Task<long?> EnsureAsync(string sub, string email, CancellationToken ct)
    {
        if (!Guid.TryParse(sub, out var cognitoSub)) return null;
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "insert into person (cognito_sub, email) values ($1, $2)" +
            " on conflict (cognito_sub) do update set email = excluded.email, last_seen_at = now()" +
            " returning id;", conn);
        command.Parameters.Add(new NpgsqlParameter { Value = cognitoSub });
        command.Parameters.Add(new NpgsqlParameter { Value = email });
        var scalar = await command.ExecuteScalarAsync(ct);
        return scalar is long l ? l : (scalar is int i ? i : (long?)null);
    }
}
