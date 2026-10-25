using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 People (Admin, capability `people`), sql.md 8.15: the blocked
// addresses and domains. Every write is audited on entity `signup_block`.
// - GET /admin/people/blocks: every block, newest first, with matchingCount
// - GET /admin/people/blocks/impact?pattern=: the person rows a block would remove
// - POST /admin/people/blocks: inserts the block, then removes every matching
//   person through PersonRemoval in the same transaction (each with its own
//   `delete` audit row on entity `person`); an address pattern with no person
//   row also removes the address's Cognito user. A Cognito failure rolls
//   everything back and answers 502 upstream_failed.
// - DELETE /admin/people/blocks/{id}: removes the block; the address may sign
//   up again as a new person.
// After either write commits, the full pattern list is published to the
// sign-up gate's secret (SignupBlockPublish); a publish failure is logged and
// leaves the response unchanged.
public static class AdminSignupBlockEndpoints
{
    private const string BlockColumns = "b.id, b.pattern, b.note, b.created_by, b.created_at";

    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapImpact(app);
        MapCreate(app);
        MapDelete(app);
    }

    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/people/blocks",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    "select " + BlockColumns + ", (select count(*) from person p where "
                    + SignupBlocks.PersonMatchesSql("b.pattern") + ") "
                    + "from signup_block b order by b.created_at desc, b.id desc;", conn);
                var items = new List<SignupBlockDto>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var dto = ReadBlock(reader);
                    dto.MatchingCount = (int)reader.GetInt64(5);
                    items.Add(dto);
                }
                return Results.Ok(new ItemsResponse<SignupBlockDto> { Items = items });
            })
            .WithTags("AdminInbox")
            .Produces<ItemsResponse<SignupBlockDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.People)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapImpact(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/people/blocks/impact",
            async (HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var (pattern, kind) = ValidatePattern(ctx.Request.Query["pattern"].ToString());
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var people = await MatchingPeopleAsync(conn, null, pattern, ct);
                var ids = people.Select(p => p.Id).ToArray();
                return Results.Ok(new SignupBlockImpactDto
                {
                    Pattern = pattern,
                    Kind = kind,
                    People = people,
                    Subscriptions = await CountForPeopleAsync(conn, "subscriber", ids, ct),
                    Cookies = await CountForPeopleAsync(conn, "cookie", ids, ct),
                });
            })
            .WithTags("AdminInbox")
            .Produces<SignupBlockImpactDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.People)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapCreate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/people/blocks",
            async (CreateSignupBlockRequest body, HttpContext ctx, AuditRecorder audit, IPersonDirectory directory,
                ISignupBlockPublisher publisher, ILoggerFactory loggers, WmsfoConnectionStrings connections,
                CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                var (pattern, kind, note) = Validate(body);
                var actor = audit.Actor();
                var logger = loggers.CreateLogger(typeof(AdminSignupBlockEndpoints).FullName!);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);

                if (await PatternExistsAsync(conn, tx, pattern, ct)) throw BlockExists();
                SignupBlockDto block;
                try
                {
                    await using var insert = new NpgsqlCommand(
                        "insert into signup_block as b (pattern, note, created_by) values ($1, $2, $3) returning "
                        + BlockColumns + ";", conn, tx);
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = pattern });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = note });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = actor });
                    await using var reader = await insert.ExecuteReaderAsync(ct);
                    await reader.ReadAsync(ct);
                    block = ReadBlock(reader);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                    && ex.ConstraintName == "signup_block_pattern_key")
                {
                    throw BlockExists();
                }

                var removed = await MatchingPeopleAsync(conn, tx, pattern, ct);
                try
                {
                    foreach (var person in removed)
                    {
                        await PersonRemoval.RemoveAsync(conn, tx, directory, audit, person.Id, actor,
                            loggers.CreateLogger(typeof(PersonRemoval).FullName!), ct);
                    }
                    if (kind == SignupBlocks.KindAddress && removed.Count == 0)
                    {
                        await RemoveAddressSignInAsync(directory, pattern, logger, ct);
                    }
                }
                catch (ApiException ex) when (ex.Code == ApiErrorCodes.UpstreamFailed)
                {
                    throw new ApiException(StatusCodes.Status502BadGateway, ApiErrorCodes.UpstreamFailed,
                        "the sign-in could not be removed; nothing was blocked");
                }

                await audit.RecordAsync(conn, tx, "create", "signup_block",
                    block.Id.ToString(CultureInfo.InvariantCulture),
                    before: null, after: AuditAfter(block, removed), actor, ct);
                await tx.CommitAsync(ct);
                await SignupBlockPublish.AfterWriteAsync(conn, publisher, logger);
                return Results.Json(block, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminInbox")
            .Accepts<CreateSignupBlockRequest>("application/json")
            .Produces<SignupBlockDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.People)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapDelete(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/people/blocks/{id:long}",
            async (long id, HttpContext ctx, AuditRecorder audit, ISignupBlockPublisher publisher,
                ILoggerFactory loggers, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                SignupBlockDto? before = null;
                await using (var read = new NpgsqlCommand(
                    "select " + BlockColumns + ", (select count(*) from person p where "
                    + SignupBlocks.PersonMatchesSql("b.pattern") + ") "
                    + "from signup_block b where b.id = $1 for update;", conn, tx))
                {
                    read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        before = ReadBlock(reader);
                        before.MatchingCount = (int)reader.GetInt64(5);
                    }
                }
                if (before is null)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "block not found");
                await using (var del = new NpgsqlCommand("delete from signup_block where id = $1;", conn, tx))
                {
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await del.ExecuteNonQueryAsync(ct);
                }
                await audit.RecordAsync(conn, tx, "delete", "signup_block",
                    id.ToString(CultureInfo.InvariantCulture), before, after: null, ct);
                await tx.CommitAsync(ct);
                await SignupBlockPublish.AfterWriteAsync(conn, publisher,
                    loggers.CreateLogger(typeof(AdminSignupBlockEndpoints).FullName!));
                return Results.NoContent();
            })
            .WithTags("AdminInbox")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.People)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // `pattern` trimmed and lowercased, an address or `*@domain`, 3 to 254;
    // `note` trimmed, 1 to 300.
    private static (string Pattern, string Kind, string Note) Validate(CreateSignupBlockRequest body)
    {
        var v = new RequestValidation();
        var pattern = SignupBlocks.Normalize(body.Pattern);
        var kind = SignupBlocks.Kind(pattern);
        if (kind is null) v.Field("pattern", PatternRule);
        var note = (body.Note ?? "").Trim();
        if (note.Length < 1 || note.Length > SignupBlocks.NoteMax)
            v.Field("note", $"must be 1 to {SignupBlocks.NoteMax} characters");
        v.ThrowIfInvalid();
        return (pattern, kind!, note);
    }

    private static (string Pattern, string Kind) ValidatePattern(string raw)
    {
        var pattern = SignupBlocks.Normalize(raw);
        var kind = SignupBlocks.Kind(pattern);
        if (kind is null) RequestValidation.Throw("pattern", PatternRule);
        return (pattern, kind!);
    }

    private const string PatternRule =
        "must be an address (jane@example.com) or a domain (*@example.com), 3 to 254 characters";

    private static ApiException BlockExists() =>
        new(StatusCodes.Status409Conflict, ApiErrorCodes.BlockExists, "this address or domain is already blocked");

    // A Cognito user who signed up but never reached the API has no row; the
    // people pools take the address as the username. A missing user is fine.
    private static async Task RemoveAddressSignInAsync(IPersonDirectory directory, string address, ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await directory.RemoveAsync(address, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("signup block: the sign-in removal failed ({ExceptionType}: {ExceptionMessage})",
                ex.GetType().Name, ex.Message);
            throw new ApiException(StatusCodes.Status502BadGateway, ApiErrorCodes.UpstreamFailed,
                "the sign-in could not be removed; nothing was blocked");
        }
    }

    private static async Task<bool> PatternExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string pattern,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select 1 from signup_block where pattern = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = pattern });
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // The person rows the pattern matches now, by id.
    private static async Task<List<SignupBlockPersonDto>> MatchingPeopleAsync(NpgsqlConnection conn,
        NpgsqlTransaction? tx, string pattern, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select p.id, p.email from person p where " + SignupBlocks.PersonMatchesSql("$1::text")
            + " order by p.id;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = pattern });
        var people = new List<SignupBlockPersonDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            people.Add(new SignupBlockPersonDto { Id = reader.GetInt64(0), Email = reader.GetString(1) });
        }
        return people;
    }

    private static async Task<int> CountForPeopleAsync(NpgsqlConnection conn, string table, long[] personIds,
        CancellationToken ct)
    {
        if (personIds.Length == 0) return 0;
        await using var cmd = new NpgsqlCommand(
            "select count(*) from " + table + " where person_id = any($1);", conn);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            Value = personIds,
        });
        return (int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    private static SignupBlockDto ReadBlock(NpgsqlDataReader reader)
    {
        var pattern = reader.GetString(1);
        return new SignupBlockDto
        {
            Id = reader.GetInt64(0),
            Pattern = pattern,
            Kind = SignupBlocks.Kind(pattern) ?? SignupBlocks.KindAddress,
            Note = reader.GetString(2),
            CreatedBy = reader.GetString(3),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(4),
        };
    }

    // `{ ...SignupBlock, removed: [{ id, email }] }`.
    private static JsonElement AuditAfter(SignupBlockDto block, List<SignupBlockPersonDto> removed)
    {
        var obj = JsonNode.Parse(JsonSerializer.Serialize(block, CanonicalJson.Options)) as JsonObject
            ?? new JsonObject();
        obj["removed"] = JsonNode.Parse(JsonSerializer.Serialize(removed, CanonicalJson.Options));
        return JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString(CanonicalJson.Options));
    }
}
