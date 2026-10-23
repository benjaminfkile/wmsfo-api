using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Email;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.Endpoints;

// contracts 4.4 registered-person endpoints. The person is upserted by the
// CognitoAuth filter (api.md 6.2); every handler reads the person id from the
// principal. Subscriptions family follows sql.md 8.12; cookies follow sql.md 8.9;
// the person state (GET /me, PUT /me/seen, PUT /me/preferences) follows sql.md 8.12a.
public static class MeEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapListSubscriptions(app);
        MapCreateSubscription(app);
        MapResendVerification(app);
        MapDeleteSubscription(app);
        MapListMyCookies(app);
        MapCreateCookie(app);
        MapGetMe(app);
        MapPutPreferences(app);
        MapPutSeen(app);
    }

    // The OpenAPI description of GET /me/notifications and PUT /me/notifications/seen,
    // which answer as GET /me and PUT /me/seen until A136 removes them.
    public const string AliasDescription = "Alias, removed once the site has moved to /me";

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
            .RequireRateLimiting(RateLimitPolicies.MeReadsPerPerson)
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
                // The account's own verified address is verified at once: no token, no email.
                var verifiedAtOnce = IsAccountVerifiedAddress(ctx.User, address);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await RequireSubscriptionsOpenAsync(conn, ct);

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
                        // A never-verified row for the account's verified address is
                        // verified now.
                        subscriberId = existingId.Value;
                        await using (var reactivate = new NpgsqlCommand(@"
update subscriber set unsubscribed_at = null,
  verified_at = case when $2 then coalesce(verified_at, now()) else verified_at end,
  updated_at = now()
where id = $1;", conn, tx))
                        {
                            reactivate.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = subscriberId });
                            reactivate.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = verifiedAtOnce });
                            await reactivate.ExecuteNonQueryAsync(ct);
                        }
                        // Only re-verify when it was never verified and is not verified now.
                        needsToken = verifiedAt is null && !verifiedAtOnce;
                    }
                    else
                    {
                        var unsubToken = Keys.MintUnsubscribeToken();
                        await using (var insert = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at)
values ($1, 'email', $2, $3, case when $4 then now() end) returning id;", conn, tx))
                        {
                            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
                            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = unsubToken.Token });
                            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = verifiedAtOnce });
                            var r = await insert.ExecuteScalarAsync(ct);
                            subscriberId = Convert.ToInt64(r ?? 0L, CultureInfo.InvariantCulture);
                        }
                        needsToken = !verifiedAtOnce;
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

    // The `subscriptions_open` setting (contracts 4.4, 6), read before any
    // write; a missing row means open. False is `409 subscriptions_closed`.
    internal static async Task RequireSubscriptionsOpenAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select value from app_setting where key = 'subscriptions_open';", conn);
        if (await cmd.ExecuteScalarAsync(ct) is string raw)
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.False)
            {
                throw new ApiException(StatusCodes.Status409Conflict,
                    ApiErrorCodes.SubscriptionsClosed, "new alert sign-ups are closed for now");
            }
        }
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
                await RequireSubscriptionsOpenAsync(conn, ct);
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
            .RequireRateLimiting(RateLimitPolicies.MeReadsPerPerson)
            .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
    }

    // GET /me/cookies. `limit` is the live setting.
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
select id, cookie_type_id, note, left_at
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
            .RequireRateLimiting(RateLimitPolicies.MeReadsPerPerson)
            .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
    }

    // POST /cookies. The whole pick in one transaction (sql.md 8.9): every type
    // checked, the limit checked against the total, one multi-row insert, then
    // the node-tally increments.
    private static void MapCreateCookie(IEndpointRouteBuilder app)
    {
        app.MapPost("/cookies",
            async (CreateCookieRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, NodeStateService state, CancellationToken ct) =>
            {
                var personId = RequirePersonId(ctx);
                var v = new RequestValidation();
                var items = body.Items ?? new List<CookiePick>();
                if (items.Count == 0) v.Field("items", "required");
                if (items.Count > 50) v.Field("items", "at most 50 entries");
                var seen = new HashSet<long>();
                var total = 0;
                for (var i = 0; i < items.Count; i += 1)
                {
                    var it = items[i];
                    if (it.CookieTypeId <= 0) v.Field($"items[{i}].cookieTypeId", "required");
                    else if (!seen.Add(it.CookieTypeId)) v.Field($"items[{i}].cookieTypeId", "listed twice");
                    if (it.Count <= 0 || it.Count > 1000) v.Field($"items[{i}].count", "must be between 1 and 1000");
                    else total += it.Count;
                }
                var note = body.Note;
                if (note is not null)
                {
                    if (note.Length > 140) v.Field("note", "must be 140 characters or fewer");
                }
                v.ThrowIfInvalid();

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                long eventId;
                int used;
                int limit;
                var left = new List<CookieLeft>(total);
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
                    // Every cookie type must exist and be active.
                    var typeIds = items.Select(i => i.CookieTypeId).ToArray();
                    var active = new HashSet<long>();
                    await using (var type = new NpgsqlCommand(
                        "select id from cookie_type where id = any($1) and active;", conn, tx))
                    {
                        type.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = typeIds });
                        await using var reader = await type.ExecuteReaderAsync(ct);
                        while (await reader.ReadAsync(ct)) active.Add(reader.GetInt64(0));
                    }
                    var missing = typeIds.Where(id => !active.Contains(id)).ToArray();
                    if (missing.Length > 0)
                        throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "cookie type not found",
                            new { cookieTypeIds = missing });
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
                    // Count this person's cookies on the event.
                    int currentCount;
                    await using (var count = new NpgsqlCommand(
                        "select count(*) from cookie where event_id = $1 and person_id = $2;", conn, tx))
                    {
                        count.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                        count.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                        var r = await count.ExecuteScalarAsync(ct);
                        currentCount = (int)Convert.ToInt64(r ?? 0L, CultureInfo.InvariantCulture);
                    }
                    var remainingBefore = Math.Max(0, limit - currentCount);
                    if (total > remainingBefore)
                        throw new ApiException(StatusCodes.Status409Conflict,
                            "cookie_limit_reached", "cookie limit reached", new { remaining = remainingBefore });

                    // One multi-row insert: the pick expanded to one row per cookie.
                    var rowTypes = new long[total];
                    var k = 0;
                    foreach (var it in items)
                    {
                        for (var c = 0; c < it.Count; c += 1) rowTypes[k++] = it.CookieTypeId;
                    }
                    await using (var insert = new NpgsqlCommand(@"
insert into cookie (event_id, person_id, cookie_type_id, note)
select $1, $2, t, $4 from unnest($3::bigint[]) as t
returning id, cookie_type_id, left_at;", conn, tx))
                    {
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = rowTypes });
                        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)note ?? DBNull.Value });
                        await using var reader = await insert.ExecuteReaderAsync(ct);
                        while (await reader.ReadAsync(ct))
                        {
                            left.Add(new CookieLeft
                            {
                                Id = reader.GetInt64(0),
                                CookieTypeId = reader.GetInt64(1),
                                LeftAt = reader.GetFieldValue<DateTimeOffset>(2),
                            });
                        }
                    }
                    used = currentCount + total;
                    await tx.CommitAsync(ct);
                }

                // After commit: increment this node's tally counter per cookie.
                foreach (var it in items)
                {
                    for (var c = 0; c < it.Count; c += 1) state.IncrementTallyDelta(it.CookieTypeId);
                }

                return Results.Json(new CreateCookieResponse
                {
                    EventId = eventId,
                    Left = total,
                    Remaining = Math.Max(0, limit - used),
                    Cookies = left,
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

    // GET /me → 200 Me (contracts 4.4). Two read-only statements on App: the
    // person row by primary key for the seen marker and the preferences, then
    // the newest open subscriber row by person_id. GET /me/notifications is an
    // alias of the same handler until the site has moved to /me.
    private static void MapGetMe(IEndpointRouteBuilder app)
    {
        foreach (var (path, alias) in new[] { ("/me", false), ("/me/notifications", true) })
        {
            var route = app.MapGet(path, GetMeAsync)
                .WithTags("Me")
                .Produces<MeResponse>(StatusCodes.Status200OK)
                .RequireAuthorization(AuthPolicies.Person)
                .RequireRateLimiting(RateLimitPolicies.MeReadsPerPerson)
                .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
            if (alias) route.WithDescription(AliasDescription);
        }
    }

    private static async Task<IResult> GetMeAsync(
        HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct)
    {
        var personId = RequirePersonId(ctx);
        await using var conn = new NpgsqlConnection(connections.App);
        await conn.OpenAsync(ct);

        long? seenUpToId = null;
        PreferencesDto? preferences = null;
        await using (var person = new NpgsqlCommand(
            "select seen_up_to_id, preferences::text from person where id = $1;", conn))
        {
            person.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
            await using var reader = await person.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0)) seenUpToId = reader.GetInt64(0);
                if (!reader.IsDBNull(1))
                {
                    preferences = JsonSerializer.Deserialize<PreferencesDto>(reader.GetString(1), CanonicalJson.Options);
                }
            }
        }

        SubscriptionDto? subscription = null;
        await using (var cmd = new NpgsqlCommand(@"
select id, channel, address, verified_at, unsubscribed_at, created_at
from subscriber
where person_id = $1 and unsubscribed_at is null
order by created_at desc, id desc
limit 1;", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                subscription = new SubscriptionDto
                {
                    Id = reader.GetInt64(0),
                    Channel = reader.GetString(1),
                    Address = reader.GetString(2),
                    VerifiedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    UnsubscribedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                };
            }
        }

        return Results.Ok(new MeResponse
        {
            Subscription = subscription,
            SeenUpToId = seenUpToId,
            Preferences = preferences,
        });
    }

    // PUT /me/preferences → 204 (contracts 4.4). MePreferencesRules validates
    // the body; the validated object, re-serialized, replaces person.preferences
    // whole in one update by primary key on App. No read, no outbox row, no
    // snapshot work, no audit row.
    private static void MapPutPreferences(IEndpointRouteBuilder app)
    {
        app.MapPut("/me/preferences",
            async ([FromBody] JsonElement body, HttpContext ctx,
                   WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var personId = RequirePersonId(ctx);
                var preferences = MePreferencesRules.Parse(body);
                var json = System.Text.Encoding.UTF8.GetString(CanonicalJson.SerializeToUtf8Bytes(preferences));

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    "update person set preferences = $1 where id = $2;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
                await cmd.ExecuteNonQueryAsync(ct);
                return Results.NoContent();
            })
            .WithTags("Me")
            .Accepts<PreferencesDto>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Person)
            .RequireRateLimiting(RateLimitPolicies.MePreferencesPerPerson)
            .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
    }

    // PUT /me/seen → 204 (contracts 4.4). One statement on App; the marker only
    // rises, so a lower or equal id updates no row and still answers 204.
    // PUT /me/notifications/seen is an alias of the same handler until the site
    // has moved to /me.
    private static void MapPutSeen(IEndpointRouteBuilder app)
    {
        foreach (var (path, alias) in new[] { ("/me/seen", false), ("/me/notifications/seen", true) })
        {
            var route = app.MapPut(path, PutSeenAsync)
                .WithTags("Me")
                .Accepts<MeSeenRequest>("application/json")
                .Produces(StatusCodes.Status204NoContent)
                .RequireAuthorization(AuthPolicies.Person)
                .RequireRateLimiting(RateLimitPolicies.MeSeenPerPerson)
                .AddEndpointFilter(CognitoAuth.PersonUpsertFilter);
            if (alias) route.WithDescription(AliasDescription);
        }
    }

    private static async Task<IResult> PutSeenAsync(
        MeSeenRequest body, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct)
    {
        var personId = RequirePersonId(ctx);
        var v = new RequestValidation();
        if (body.SeenUpToId is null) v.Field("seenUpToId", "required");
        else if (body.SeenUpToId.Value <= 0) v.Field("seenUpToId", "must be greater than 0");
        v.ThrowIfInvalid();
        var seenUpToId = body.SeenUpToId!.Value;

        await using var conn = new NpgsqlConnection(connections.App);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
update person set seen_up_to_id = $1
where id = $2 and (seen_up_to_id is null or seen_up_to_id < $1);", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = seenUpToId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        await cmd.ExecuteNonQueryAsync(ct);
        return Results.NoContent();
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

    // contracts 4.4: the address is the account's verified address when it equals
    // the id token's `email` claim trimmed and lowercased and the token's
    // `email_verified` claim is true (a boolean claim arrives as the string
    // "true"). An absent claim or any other value is not verified.
    internal static bool IsAccountVerifiedAddress(System.Security.Claims.ClaimsPrincipal user, string address)
    {
        var email = user.FindFirst(PersonClaims.Email)?.Value;
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (!string.Equals(email.Trim().ToLowerInvariant(), address, StringComparison.Ordinal)) return false;
        var verified = user.FindFirst(PersonClaims.EmailVerified)?.Value;
        return bool.TryParse(verified, out var isVerified) && isVerified;
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
