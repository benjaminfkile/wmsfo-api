using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Endpoints.Impact;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints;

// Contracts 4.5 People, sql.md 8.15: removing a person removes their sign-in
// first, then their rows. Inside the caller's transaction: lock the row, take
// the delete impact, call IPersonDirectory.RemoveAsync with the row's sub
// (global sign-out, then delete the Cognito user), and only then delete the
// row with its cascade (subscriber, cookie) and write the audit row, whose
// `before` is `{ ...Person, impact, signIn }`. A directory failure other than
// a missing user is `502 upstream_failed` before any delete statement, so the
// caller's rollback leaves every row in place. Returns null when no row has
// the id; the caller commits.
public static class PersonRemoval
{
    public static async Task<SignInRemoval?> RemoveAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IPersonDirectory directory,
        AuditRecorder audit,
        long id,
        string actor,
        ILogger logger,
        CancellationToken ct)
    {
        PersonDto? before = null;
        Guid sub = default;
        await using (var read = new NpgsqlCommand(
            "select id, email, created_at, last_seen_at, cognito_sub from person where id = $1 for update;", conn, tx))
        {
            read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                before = new PersonDto
                {
                    Id = reader.GetInt64(0),
                    Email = reader.GetString(1),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(2),
                    LastSeenAt = reader.GetFieldValue<DateTimeOffset>(3),
                };
                sub = reader.GetGuid(4);
            }
        }
        if (before is null) return null;

        var impact = await PersonImpactQueries.PreviewAsync(conn, tx, id, ct);

        SignInRemoval signIn;
        try
        {
            signIn = await directory.RemoveAsync(sub.ToString(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("person delete: the sign-in removal failed ({ExceptionType}: {ExceptionMessage})",
                ex.GetType().Name, ex.Message);
            throw new ApiException(StatusCodes.Status502BadGateway, ApiErrorCodes.UpstreamFailed,
                "the sign-in could not be removed; nothing was deleted");
        }

        await PersonImpactQueries.ApplyAsync(conn, tx, id, ct);
        await using (var del = new NpgsqlCommand("delete from person where id = $1;", conn, tx))
        {
            del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await del.ExecuteNonQueryAsync(ct);
        }
        await audit.RecordAsync(conn, tx, "delete", "person",
            id.ToString(CultureInfo.InvariantCulture),
            before: AuditBefore(before, impact, signIn), after: null, actor, ct);
        return signIn;
    }

    // `{ ...Person, impact, signIn }` with `signIn` "removed" or "absent".
    public static JsonElement AuditBefore(PersonDto person, DeleteImpactDto impact, SignInRemoval signIn)
    {
        var combined = ImpactBefore.Combine(person, impact);
        var obj = JsonNode.Parse(combined.GetRawText()) as JsonObject ?? new JsonObject();
        obj["signIn"] = signIn == SignInRemoval.Removed ? "removed" : "absent";
        return JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString(CanonicalJson.Options));
    }
}
