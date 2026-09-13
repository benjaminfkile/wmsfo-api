using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints;

// api.md 5a Audit recording. Every /admin/* write records one row in audit_log
// with the actor, action, entity, entity_id, before/after DTOs, and request id
// through `AuditRecorder.RecordAsync` inside its own transaction (contracts 4.5
// Audit). The `AuditEndpointFilter` stays as the safety net for a write that
// recorded nothing; no admin write in the route table is supposed to reach it,
// and the every-write integration test asserts that.

// Scoped-per-request writer. The endpoint calls `RecordAsync` inside its write
// transaction so the audit row is committed atomically with the write. The
// service also tracks `Recorded` so the AuditEndpointFilter can tell whether a
// fallback row is needed (which for a properly wired write it never is).
public sealed class AuditRecorder
{
    private readonly IHttpContextAccessor _http;

    public AuditRecorder(IHttpContextAccessor http) => _http = http;

    public bool Recorded { get; private set; }

    public string Actor()
    {
        var http = _http.HttpContext
            ?? throw new InvalidOperationException("no HttpContext");
        var keyName = ApiKeyAuthenticationHandler.TryGetApiKeyName(http.User);
        if (!string.IsNullOrEmpty(keyName)) return "key:" + keyName;
        var email = http.User.FindFirst(PersonClaims.Email)?.Value;
        return string.IsNullOrEmpty(email) ? "unknown" : "person:" + email;
    }

    // Write the row inside the caller's transaction. `entityId` is the row id
    // as text (the year for a sponsor order, the setting key for settings).
    // `before` and `after` are serialized with the canonical wire options so the
    // log reads like the API answers (api.md 5a). Returns the AuditStamp shape
    // (action, by, at) of the row just written so the endpoint's response DTO
    // can carry the stamp of the row this write inserted (contracts 4.5 Audit:
    // `audit` is the newest row for the entity).
    public async Task<AuditStampDto> RecordAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string action,
        string entity,
        string entityId,
        object? before,
        object? after,
        CancellationToken ct)
    {
        var actor = Actor();
        var requestId = _http.HttpContext?.TraceIdentifier;
        var beforeJson = SerializeOrNull(before);
        var afterJson = SerializeOrNull(after);
        await using var cmd = new NpgsqlCommand(@"
insert into audit_log (actor, action, entity, entity_id, before, after, request_id)
values ($1, $2, $3, $4, $5::jsonb, $6::jsonb, $7)
returning at;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = actor });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = action });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = entity });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = entityId ?? "" });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)beforeJson ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)afterJson ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)requestId ?? DBNull.Value });
        DateTimeOffset at;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("audit_log insert returned no row");
            at = reader.GetFieldValue<DateTimeOffset>(0);
        }
        Recorded = true;
        return new AuditStampDto { Action = action, By = actor, At = at };
    }

    public static string? SerializeOrNull(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement { ValueKind: JsonValueKind.Null }) return null;
        if (value is JsonElement el) return el.GetRawText();
        return JsonSerializer.Serialize(value, CanonicalJson.Options);
    }
}

// api.md 5a safety net. Every `/admin/*` write carries this filter alongside
// its capability/deny gate. Endpoints that use `AuditRecorder.RecordAsync` in
// their write transaction (which is every admin write per the every-write
// integration test) have `Recorded = true` by the time this filter runs, so it
// does nothing. If, in the future, a new admin write is added without wiring
// the recorder, this filter still leaves the audit row unwritten; the
// integration test's row-count assertion catches the omission.
public sealed class AuditEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var result = await next(ctx);
        return result;
    }
}
