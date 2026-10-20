using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.Endpoints;

// contracts 4.3 public writes.
// POST /contact - sql.md 8.13: one PUT of the inbox object under
// contact-inbox/; no database statement. The leader's ContactInboxImporter
// moves it into contact_message and the outbox.
// POST /subscriptions/verify - sql.md 8.12 autocommit verify.
// POST /subscriptions/unsubscribe - token from query or JSON, also
// form-urlencoded per RFC 8058 (the form body is ignored).
public static class PublicWriteEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapContact(app);
        MapVerify(app);
        MapUnsubscribe(app);
    }

    // POST /contact: 202 { receivedAt }. name 1..100, email 3..254, message
    // 1..2000. One PUT of the inbox object, no database statement.
    private static void MapContact(IEndpointRouteBuilder app)
    {
        app.MapPost("/contact",
            async (ContactRequest body, HttpContext ctx, IObjectStore store,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                var name = body.Name?.Trim() ?? "";
                var email = (body.Email ?? "").Trim();
                var message = body.Message ?? "";
                if (!ContactInbox.ValidName(name))
                    v.Field("name", "must be 1 to 100 characters");
                if (!ContactInbox.ValidEmail(email))
                    v.Field("email", "must be a valid email address");
                if (!ContactInbox.ValidMessage(message))
                    v.Field("message", "must be 1 to 2000 characters");
                v.ThrowIfInvalid();

                var receivedAt = ContactInbox.Now(DateTimeOffset.UtcNow);
                var inbox = new ContactInboxObject
                {
                    Name = name,
                    Email = email,
                    Message = message,
                    ClientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    ReceivedAt = receivedAt,
                };
                var key = ContactInbox.KeyFor(receivedAt);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(SnapshotBuilder.PutTimeout);
                    await store.PutObjectAsync(key, CanonicalJson.SerializeToUtf8Bytes(inbox),
                        ContactInbox.ContentType, ContactInbox.CacheControl, tag: null, cts.Token);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    loggers.CreateLogger(typeof(PublicWriteEndpoints).FullName!)
                        .LogWarning(ex, "contact PUT failed; key={Key}", key);
                    throw new ApiException(StatusCodes.Status502BadGateway, ContactInbox.WriteFailedCode,
                        "contact message upload failed");
                }

                // Canonical options, so receivedAt reads as the object carries it.
                return Results.Json(new ContactResponse { ReceivedAt = receivedAt }, CanonicalJson.Options,
                    statusCode: StatusCodes.Status202Accepted);
            })
            .WithTags("Public")
            .Accepts<ContactRequest>("application/json")
            .Produces<ContactResponse>(StatusCodes.Status202Accepted)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireRateLimiting(RateLimitPolicies.ContactPerNode);
    }

    // POST /subscriptions/verify: 200 with verifiedAt (existing or new). The three
    // cases: already verified (200 with the existing value), unverified and
    // unexpired (set verified_at, 200), unverified expired or unknown (404).
    private static void MapVerify(IEndpointRouteBuilder app)
    {
        app.MapPost("/subscriptions/verify",
            async (SubscriptionVerifyRequest body, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var token = body?.Token ?? "";
                if (!IsToken(token, Keys.SubscribeVerifyPrefix))
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "token unknown");
                var hash = Keys.Hash(token);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                long id;
                DateTimeOffset? verifiedAt;
                DateTimeOffset? verifyExpiresAt;
                await using (var lookup = new NpgsqlCommand(@"
select id, verified_at, verify_expires_at from subscriber where verify_token_hash = $1;", conn))
                {
                    lookup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = hash });
                    await using var reader = await lookup.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct))
                        throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "token unknown");
                    id = reader.GetInt64(0);
                    verifiedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
                    verifyExpiresAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
                }

                if (verifiedAt is not null)
                {
                    return Results.Ok(new SubscriptionVerifyResponse { VerifiedAt = verifiedAt.Value });
                }
                if (verifyExpiresAt is null || verifyExpiresAt.Value <= DateTimeOffset.UtcNow)
                {
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "token expired");
                }
                DateTimeOffset newVerifiedAt;
                await using (var upd = new NpgsqlCommand(@"
update subscriber set verified_at = now(), updated_at = now()
where id = $1 and verified_at is null returning verified_at;", conn))
                {
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await upd.ExecuteScalarAsync(ct);
                    // Concurrent verify may have set verified_at already; re-read.
                    if (r is null || r is DBNull)
                    {
                        await using var reread = new NpgsqlCommand(
                            "select verified_at from subscriber where id = $1;", conn);
                        reread.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r2 = await reread.ExecuteScalarAsync(ct);
                        newVerifiedAt = AsDateTimeOffset(r2) ?? DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        newVerifiedAt = AsDateTimeOffset(r) ?? DateTimeOffset.UtcNow;
                    }
                }
                return Results.Ok(new SubscriptionVerifyResponse { VerifiedAt = newVerifiedAt });
            })
            .WithTags("Public")
            .Accepts<SubscriptionVerifyRequest>("application/json")
            .Produces<SubscriptionVerifyResponse>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireRateLimiting(RateLimitPolicies.SubscriptionsPerToken);
    }

    // POST /subscriptions/unsubscribe: token from query ?token=wsu_...
    // JSON body { token: "wsu_..." }
    // or application/x-www-form-urlencoded (RFC 8058) whose body is ignored.
    // Idempotent; 204 on success, 404 on unknown token.
    private static void MapUnsubscribe(IEndpointRouteBuilder app)
    {
        app.MapPost("/subscriptions/unsubscribe",
            async (HttpRequest request, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var token = await ResolveUnsubscribeTokenAsync(request, ct);
                if (!IsToken(token, Keys.SubscribeUnsubscribePrefix))
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "token unknown");

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(@"
update subscriber
set unsubscribed_at = coalesce(unsubscribed_at, now()), updated_at = now()
where unsubscribe_token = $1 returning id;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = token });
                var r = await cmd.ExecuteScalarAsync(ct);
                if (r is null || r is DBNull)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "token unknown");
                return Results.NoContent();
            })
            .WithTags("Public")
            .Produces(StatusCodes.Status204NoContent)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireRateLimiting(RateLimitPolicies.SubscriptionsPerToken);
    }

    private static async Task<string> ResolveUnsubscribeTokenAsync(HttpRequest request, CancellationToken ct)
    {
        var fromQuery = request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(fromQuery)) return fromQuery;
        var contentType = request.ContentType?.ToLowerInvariant() ?? "";
        if (contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.Ordinal))
        {
            // RFC 8058: List-Unsubscribe=One-Click. The form body is ignored; the
            // token is expected on the query string. If the client also placed
            // token in the form, honour it.
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(ct);
                var formToken = form["token"].ToString();
                if (!string.IsNullOrEmpty(formToken)) return formToken;
            }
            return "";
        }
        if (contentType.StartsWith("application/json", StringComparison.Ordinal) || string.IsNullOrEmpty(contentType))
        {
            if (request.ContentLength is 0) return "";
            try
            {
                var body = await JsonSerializer.DeserializeAsync<SubscriptionUnsubscribeRequest>(
                    request.Body, Wmsfo.Api.Objects.CanonicalJson.Options, ct);
                return body?.Token ?? "";
            }
            catch (JsonException)
            {
                return "";
            }
        }
        return "";
    }

    // Npgsql returns DateTime (Kind=Utc) for timestamptz via untyped
    // ExecuteScalarAsync; explicit converters like `(DateTimeOffset)r` throw
    // InvalidCastException on the boxed DateTime. Convert here.
    private static DateTimeOffset? AsDateTimeOffset(object? r) => r switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        _ => null,
    };

    private static bool IsToken(string value, string prefix)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (value.Length != prefix.Length + Keys.TokenBodyChars) return false;
        for (var i = prefix.Length; i < value.Length; i++)
        {
            var c = value[i];
            var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '-' || c == '_';
            if (!ok) return false;
        }
        return true;
    }
}
