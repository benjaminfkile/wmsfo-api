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
using Wmsfo.Api.Node;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.Endpoints;

// contracts 4.4 registered-person endpoints. The person is upserted by the
// CognitoAuth filter (api.md 6.2); every handler reads the person id from the
// principal. Subscriptions family follows sql.md 8.12; cookies follow sql.md 8.9.
public static class MeEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapMe(app);
        MapListSubscriptions(app);
        MapCreateSubscription(app);
        MapResendVerification(app);
        MapDeleteSubscription(app);
        MapListMyCookies(app);
        MapCreateCookie(app);
    }

    // GET /me → 200 { person, isAdmin }.
    private static void MapMe(IEndpointRouteBuilder app)
    {
        app.MapGet("/me",
            async (HttpContext ctx, WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                var personId = RequirePersonId(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var person = await ReadPersonAsync(conn, personId, ct)
                    ?? throw new ApiException(StatusCodes.Status401Unauthorized,
                        ApiErrorCodes.Unauthenticated, "person not found");
                var isAdmin = HasGroup(ctx, options.AdminGroup);
                return Results.Ok(new MeResponse { Person = person, IsAdmin = isAdmin });
            })
            .WithTags("Me")
            .Produces<MeResponse>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Person)
            .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
    }

    // GET /me/subscriptions → 200 { items: Subscription[] }.
    private static void MapListSubscriptions(IEndpointRouteBuilder app)
    {
        app.MapGet("/me/subscriptions",
            async (HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var personId = RequirePersonId(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<SubscriptionDto>();
                await using var cmd = new NpgsqlCommand(@"
select id, channel, address, verified_at, unsubscribed_at, created_at
from subscriber
where person_id = $1
order by id asc;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(new SubscriptionDto
                    {
                        Id = reader.GetInt64(0),
                        Channel = reader.GetString(1),
                        Address = reader.GetString(2),
                        VerifiedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                        UnsubscribedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                        CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                    });
                }
                return Results.Ok(new ItemsResponse<SubscriptionDto> { Items = items });
            })
            .WithTags("Me")
            .Produces<ItemsResponse<SubscriptionDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Person)
            .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
    }

    // POST /me/subscriptions [contracts 4.4, sql.md 8.12].
    private static void MapCreateSubscription(IEndpointRouteBuilder app)
    {
        app.MapPost("/me/subscriptions",
            async (CreateSubscriptionRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var personId = RequirePersonId(ctx);
                var (channel, address) = ValidateSubscriptionRequest(body);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                SubscriptionDto dto;
                int status;
                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    long? existingId = null;
                    long? existingPersonId = null;
                    DateTimeOffset? verifiedAt = null;
                    DateTimeOffset? unsubscribedAt = null;
                    await using (var lookup = new NpgsqlCommand(@"
select id, person_id, verified_at, unsubscribed_at
from subscriber
where channel = 'email' and address = $1
for update;", conn, tx))
                    {
                        lookup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
                        await using var reader = await lookup.ExecuteReaderAsync(ct);
                        if (await reader.ReadAsync(ct))
                        {
                            existingId = reader.GetInt64(0);
                            existingPersonId = reader.GetInt64(1);
                            verifiedAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
                            unsubscribedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
                        }
                    }

                    long subscriberId;
                    bool needsToken;
                    if (existingId is not null)
                    {
                        if (existingPersonId!.Value != personId)
                        {
                            throw new ApiException(StatusCodes.Status409Conflict,
                                "address_taken", "address belongs to another person");
                        }
                        // Same person, verified, not unsubscribed → 409 already_subscribed.
                        if (verifiedAt is not null && unsubscribedAt is null)
                        {
                            throw new ApiException(StatusCodes.Status409Conflict,
                                "already_subscribed", "address is already subscribed");
                        }
                        // Same person, unverified, not unsubscribed → 200 the existing row, no token.
                        if (verifiedAt is null && unsubscribedAt is null)
                        {
                            var existing = await ReadSubscriptionAsync(conn, tx, existingId.Value, ct);
                            await tx.CommitAsync(ct);
                            return Results.Ok(existing);
                        }
                        // Same person, unsubscribed: clear unsubscribed_at, keep verified_at.
                        subscriberId = existingId.Value;
                        await using (var reactivate = new NpgsqlCommand(@"
update subscriber set unsubscribed_at = null, updated_at = now()
where id = $1;", conn, tx))
                        {
                            reactivate.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = subscriberId });
                            await reactivate.ExecuteNonQueryAsync(ct);
                        }
                        // Only re-verify when it was never verified.
                        needsToken = verifiedAt is null;
                    }
                    else
                    {
                        var unsubToken = Keys.MintUnsubscribeToken();
                        await using (var insert = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token)
values ($1, 'email', $2, $3) returning id;", conn, tx))
                        {
                            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
                            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = unsubToken.Token });
                            var r = await insert.ExecuteScalarAsync(ct);
                            subscriberId = Convert.ToInt64(r ?? 0L, CultureInfo.InvariantCulture);
                        }
                        needsToken = true;
                    }

                    if (needsToken)
                    {
                        var verify = Keys.MintVerifyToken();
                        await using (var tokenUpd = new NpgsqlCommand(@"
update subscriber set verify_token_hash = $1, verify_expires_at = now() + interval '24 hours', updated_at = now()
where id = $2;", conn, tx))
                        {
                            tokenUpd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = verify.Hash });
                            tokenUpd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = subscriberId });
                            await tokenUpd.ExecuteNonQueryAsync(ct);
                        }
                        await InsertVerifyOutboxAsync(conn, tx, subscriberId, verify.Token, ct);
                    }

                    dto = await ReadSubscriptionAsync(conn, tx, subscriberId, ct);
                    await tx.CommitAsync(ct);
                }

                status = StatusCodes.Status201Created;
                return Results.Json(dto, statusCode: status);
            })
            .WithTags("Me")
            .Accepts<CreateSubscriptionRequest>("application/json")
            .Produces<SubscriptionDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Person)
            .RequireRateLimiting(RateLimitPolicies.MeSubscribePerPerson)
            .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
    }

    // POST /me/subscriptions/{id}/resend-verification.
    private static void MapResendVerification(IEndpointRouteBuilder app)
    {
        app.MapPost("/me/subscriptions/{id:long}/resend-verification",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var personId = RequirePersonId(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                SubscriptionDto dto;
                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    DateTimeOffset? verifiedAt = null;
                    bool rowExists = false;
                    await using (var lookup = new NpgsqlCommand(@"
select verified_at from subscriber where id = $1 and person_id = $2 for update;", conn, tx))
                    {
                        lookup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        lookup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                        await using var reader = await lookup.ExecuteReaderAsync(ct);
                        if (await reader.ReadAsync(ct))
                        {
                            rowExists = true;
                            verifiedAt = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
                        }
                    }
                    if (!rowExists)
                        throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "subscription not found");
                    if (verifiedAt is not null)
                        throw new ApiException(StatusCodes.Status409Conflict, "already_verified", "already verified");

                    var verify = Keys.MintVerifyToken();
                    await using (var upd = new NpgsqlCommand(@"
update subscriber set verify_token_hash = $1, verify_expires_at = now() + interval '24 hours', updated_at = now()
where id = $2;", conn, tx))
                    {
                        upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = verify.Hash });
                        upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        await upd.ExecuteNonQueryAsync(ct);
                    }
                    await InsertVerifyOutboxAsync(conn, tx, id, verify.Token, ct);
                    dto = await ReadSubscriptionAsync(conn, tx, id, ct);
                    await tx.CommitAsync(ct);
                }
                return Results.Json(dto, statusCode: StatusCodes.Status202Accepted);
            })
            .WithTags("Me")
            .Produces<SubscriptionDto>(StatusCodes.Status202Accepted)
            .RequireAuthorization(AuthPolicies.Person)
            .RequireRateLimiting(RateLimitPolicies.MeSubscribePerPerson)
            .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
    }

    // DELETE /me/subscriptions/{id}. Idempotent - 204 on already-unsubscribed row.
    private static void MapDeleteSubscription(IEndpointRouteBuilder app)
    {
        app.MapDelete("/me/subscriptions/{id:long}",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var personId = RequirePersonId(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(@"
update subscriber set unsubscribed_at = coalesce(unsubscribed_at, now()), updated_at = now()
where id = $1 and person_id = $2 returning id;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                var r = await cmd.ExecuteScalarAsync(ct);
                if (r is null || r is DBNull)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "subscription not found");
                return Results.NoContent();
            })
            .WithTags("Me")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Person)
            .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
    }

    // GET /me/cookies. `used` counts hidden; `limit` is the live setting.
    private static void MapListMyCookies(IEndpointRouteBuilder app)
    {
        app.MapGet("/me/cookies",
            async (HttpContext ctx, WmsfoConnectionStrings connections, NodeStateService state, CancellationToken ct) =>
            {
                var personId = RequirePersonId(ctx);
                var limit = state.Current.Settings.CookieLimitPerPerson;

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                long? eventId = null;
                int? statusId = null;
                await using (var evt = new NpgsqlCommand(
                    "select id, status_id from event where is_current;", conn))
                await using (var reader = await evt.ExecuteReaderAsync(ct))
                {
                    if (await reader.ReadAsync(ct))
                    {
                        eventId = reader.GetInt64(0);
                        statusId = reader.GetInt16(1);
                    }
                }
                if (eventId is null)
                {
                    return Results.Ok(new MyCookiesResponse
                    {
                        EventId = null,
                        EventStatusId = null,
                        Limit = limit,
                        Used = 0,
                        Remaining = Math.Max(0, limit),
                        Items = new List<MyCookieItem>(),
                    });
                }

                var items = new List<MyCookieItem>();
                await using (var cmd = new NpgsqlCommand(@"
select id, cookie_type_id, note, left_at, hidden_at
from cookie
where event_id = $1 and person_id = $2
order by id asc;", conn))
                {
                    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId.Value });
                    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        items.Add(new MyCookieItem
                        {
                            Id = reader.GetInt64(0),
                            CookieTypeId = reader.GetInt64(1),
                            Note = reader.IsDBNull(2) ? null : reader.GetString(2),
                            LeftAt = reader.GetFieldValue<DateTimeOffset>(3),
                            HiddenAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                        });
                    }
                }
                var used = items.Count;
                return Results.Ok(new MyCookiesResponse
                {
                    EventId = eventId,
                    EventStatusId = statusId,
                    Limit = limit,
                    Used = used,
                    Remaining = Math.Max(0, limit - used),
                    Items = items,
                });
            })
            .WithTags("Me")
            .Produces<MyCookiesResponse>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Person)
            .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
    }

    // POST /cookies. Transaction from sql.md 8.9 followed by node-tally increment.
    private static void MapCreateCookie(IEndpointRouteBuilder app)
    {
        app.MapPost("/cookies",
            async (CreateCookieRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, NodeStateService state, CancellationToken ct) =>
            {
                var personId = RequirePersonId(ctx);
                var v = new RequestValidation();
                if (body.CookieTypeId <= 0) v.Field("cookieTypeId", "required");
                var note = body.Note;
                if (note is not null)
                {
                    if (note.Length > 140) v.Field("note", "must be 140 characters or fewer");
                }
                v.ThrowIfInvalid();

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                long newId;
                DateTimeOffset leftAt;
                long eventId;
                int used;
                int limit;
                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    // Serialize this person's inserts.
                    await using (var lockPerson = new NpgsqlCommand(
                        "select id from person where id = $1 for update;", conn, tx))
                    {
                        lockPerson.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                        var r = await lockPerson.ExecuteScalarAsync(ct);
                        if (r is null || r is DBNull)
                            throw new ApiException(StatusCodes.Status401Unauthorized,
                                ApiErrorCodes.Unauthenticated, "person not found");
                    }
                    // Load the live event (status_id = 3).
                    await using (var live = new NpgsqlCommand(
                        "select id from event where status_id = 3;", conn, tx))
                    {
                        var r = await live.ExecuteScalarAsync(ct);
                        if (r is null || r is DBNull)
                            throw new ApiException(StatusCodes.Status409Conflict, "no_live_event", "no live event");
                        eventId = Convert.ToInt64(r, CultureInfo.InvariantCulture);
                    }
                    // Cookie type must exist and be active.
                    await using (var type = new NpgsqlCommand(
                        "select id from cookie_type where id = $1 and active;", conn, tx))
                    {
                        type.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.CookieTypeId });
                        var r = await type.ExecuteScalarAsync(ct);
                        if (r is null || r is DBNull)
                            throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "cookie type not found");
                    }
                    // Read the live limit inside the transaction.
                    limit = 10;
                    await using (var setting = new NpgsqlCommand(
                        "select value from app_setting where key = 'cookie_limit_per_person';", conn, tx))
                    {
                        var r = await setting.ExecuteScalarAsync(ct);
                        if (r is string s)
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(s);
                            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Number
                                && doc.RootElement.TryGetInt32(out var parsed))
                                limit = parsed;
                        }
                    }
                    // Count this person's cookies on the event (hidden or not).
                    int currentCount;
                    await using (var count = new NpgsqlCommand(
                        "select count(*) from cookie where event_id = $1 and person_id = $2;", conn, tx))
                    {
                        count.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                        count.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                        var r = await count.ExecuteScalarAsync(ct);
                        currentCount = (int)Convert.ToInt64(r ?? 0L, CultureInfo.InvariantCulture);
                    }
                    if (currentCount >= limit)
                        throw new ApiException(StatusCodes.Status409Conflict,
                            "cookie_limit_reached", "cookie limit reached");

                    await using (var insert = new NpgsqlCommand(@"
insert into cookie (event_id, person_id, cookie_type_id, note)
values ($1, $2, $3, $4)
returning id, left_at;", conn, tx))
                    {
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.CookieTypeId });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)note ?? DBNull.Value });
                        await using var reader = await insert.ExecuteReaderAsync(ct);
                        await reader.ReadAsync(ct);
                        newId = reader.GetInt64(0);
                        leftAt = reader.GetFieldValue<DateTimeOffset>(1);
                    }
                    used = currentCount + 1;
                    await tx.CommitAsync(ct);
                }

                // After commit: increment this node's tally counter for the type.
                state.IncrementTallyDelta(body.CookieTypeId);

                return Results.Json(new CreateCookieResponse
                {
                    Id = newId,
                    EventId = eventId,
                    CookieTypeId = body.CookieTypeId,
                    Note = note,
                    LeftAt = leftAt,
                    Remaining = Math.Max(0, limit - used),
                }, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("Me")
            .Accepts<CreateCookieRequest>("application/json")
            .Produces<CreateCookieResponse>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Person)
            .RequireRateLimiting(RateLimitPolicies.CookiesPerPerson)
            .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
    }

    // -------- helpers --------

    private static long RequirePersonId(HttpContext ctx)
    {
        var id = CognitoAuth.TryGetPersonId(ctx.User);
        if (id is null)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized,
                ApiErrorCodes.Unauthenticated, "person id missing");
        }
        return id.Value;
    }

    private static bool HasGroup(HttpContext ctx, string group)
    {
        foreach (var claim in ctx.User.FindAll(PersonClaims.Groups))
        {
            if (string.Equals(claim.Value, group, StringComparison.Ordinal)) return true;
            foreach (var part in claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (string.Equals(part, group, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static async Task<PersonDto?> ReadPersonAsync(NpgsqlConnection conn, long personId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select id, email, created_at, last_seen_at from person where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PersonDto
        {
            Id = reader.GetInt64(0),
            Email = reader.GetString(1),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(2),
            LastSeenAt = reader.GetFieldValue<DateTimeOffset>(3),
        };
    }

    private static async Task<SubscriptionDto> ReadSubscriptionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select id, channel, address, verified_at, unsubscribed_at, created_at
from subscriber where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new SubscriptionDto
        {
            Id = reader.GetInt64(0),
            Channel = reader.GetString(1),
            Address = reader.GetString(2),
            VerifiedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            UnsubscribedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
        };
    }

    private static async Task InsertVerifyOutboxAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long subscriberId, string plaintextToken, CancellationToken ct)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            subscriberId,
            verifyToken = plaintextToken,
        });
        await using var cmd = new NpgsqlCommand(@"
insert into outbox (topic, payload) values ('subscription.verify', $1::jsonb);", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = payload });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // contracts 4.4: channel must be email, address trimmed and lowercased before
    // validation and storage. Simple RFC-flavour address check.
    private static (string channel, string address) ValidateSubscriptionRequest(CreateSubscriptionRequest body)
    {
        var v = new RequestValidation();
        var channel = body.Channel ?? "";
        if (!string.Equals(channel, "email", StringComparison.Ordinal))
            v.Field("channel", "must be email");
        var raw = body.Address ?? "";
        var address = raw.Trim().ToLowerInvariant();
        if (address.Length < 3 || address.Length > 254 || !LooksLikeEmail(address))
            v.Field("address", "must be a valid email");
        v.ThrowIfInvalid();
        return (channel, address);
    }

    private static bool LooksLikeEmail(string address)
    {
        var at = address.IndexOf('@');
        if (at <= 0 || at != address.LastIndexOf('@') || at == address.Length - 1) return false;
        var local = address.AsSpan(0, at);
        var domain = address.AsSpan(at + 1);
        if (local.IsEmpty || domain.IsEmpty) return false;
        if (domain.IndexOf('.') < 0) return false;
        foreach (var c in address)
        {
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ',' || c == ';') return false;
        }
        return true;
    }
}
