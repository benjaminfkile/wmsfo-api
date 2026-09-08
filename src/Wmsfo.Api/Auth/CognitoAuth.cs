using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Auth;

// api.md 6.2: JwtBearer for Cognito ID tokens. Issuer, audiences, token_use=id,
// 60 s skew. The person-upsert filter runs after validation and attaches
// wmsfo_person_id to the request. Policies are added by the pipeline extension.
public static class CognitoAuth
{
    public static JwtBearerOptions Configure(JwtBearerOptions options, WmsfoOptions cfg)
    {
        options.Authority = cfg.CognitoIssuer;
        options.RequireHttpsMetadata = cfg.Env != "dev";
        options.SaveToken = false;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new()
        {
            ValidIssuer = cfg.CognitoIssuer,
            ValidAudiences = cfg.CognitoClientIdList(),
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
