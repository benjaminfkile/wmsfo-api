using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints;

// api.md 5a Audit recording. Every /admin/* write records one row in audit_log
// with the actor, action, entity, entity_id, before/after DTOs, and request id.
// The scoped `AuditRecorder` is handed the open transaction by the endpoint's
// write path; it also carries a pending intent so an endpoint filter can
// finalize the row on a separate connection when the endpoint's own transaction
// finished elsewhere. Endpoints attach `WithAudit(action, entity)` to declare
// the entity kind and default action; endpoints override the action or provide
// an explicit entityId via `AuditRecorder.SetAction`, `.SetEntityId`, and
// `.Record`.

public sealed class AuditEndpointMetadata
{
    public string Action { get; }
    public string Entity { get; }
    public AuditEndpointMetadata(string action, string entity)
    {
        Action = action;
        Entity = entity;
    }
}

public static class AuditEndpointExtensions
{
    // Attach the endpoint's default action and its entity kind (contracts 4.5
    // Audit). Every admin write carries one call. The AuditEndpointFilter reads
    // this to record a fallback row after the response when the endpoint's own
    // write path did not.
    public static TBuilder WithAudit<TBuilder>(this TBuilder builder, string action, string entity)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.WithMetadata(new AuditEndpointMetadata(action, entity));
    }
}

// Scoped-per-request writer. The endpoint calls `RecordAsync` inside its write
// transaction so the audit row is committed atomically with the write. The
// service also tracks state (`Recorded`, `Action`, `EntityId`, `Before`,
// `After`) that the AuditEndpointFilter reads to write a fallback row when the
// endpoint's own transaction has already committed and no audit row was
// recorded, or when the caller wants the filter to finalize the write on its
// behalf.
public sealed class AuditRecorder
{
    private readonly IHttpContextAccessor _http;

    public AuditRecorder(IHttpContextAccessor http) => _http = http;

    public bool Recorded { get; private set; }
    public string? PendingAction { get; set; }
    public string? PendingEntityId { get; set; }
    public JsonElement? PendingBefore { get; set; }
    public JsonElement? PendingAfter { get; set; }

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
    // log reads like the API answers (api.md 5a).
    public async Task RecordAsync(
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
values ($1, $2, $3, $4, $5::jsonb, $6::jsonb, $7);", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = actor });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = action });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = entity });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = entityId ?? "" });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)beforeJson ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)afterJson ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)requestId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
        Recorded = true;
    }

    // Convenience wrapper for the endpoint filter's fallback path: opens a new
    // connection, writes the row, closes. Used when the caller does not have a
    // transaction (chores, endpoints that already committed).
    public async Task RecordOnNewConnectionAsync(
        WmsfoConnectionStrings connections,
        string action,
        string entity,
        string entityId,
        object? before,
        object? after,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connections.App);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RecordAsync(conn, tx, action, entity, entityId, before, after, ct);
        await tx.CommitAsync(ct);
    }

    public static string? SerializeOrNull(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement { ValueKind: JsonValueKind.Null }) return null;
        if (value is JsonElement el) return el.GetRawText();
        return JsonSerializer.Serialize(value, CanonicalJson.Options);
    }
}

// api.md 5a fallback. Every `/admin/*` write carries this filter. After the
// endpoint's success, the filter looks at what the endpoint's write path
// recorded via AuditRecorder. If nothing was recorded, the filter writes a
// minimal row (before/after null, entityId derived from route values, action
// and entity derived from the endpoint's route pattern and HTTP method) so a
// new admin write is auditable without explicit wiring; endpoints that call
// AuditRecorder.RecordAsync directly control both before and after.
public sealed class AuditEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var isAdminWrite = IsAdminWrite(http);
        var result = await next(ctx);
        if (!isAdminWrite) return result;
        if (!IsSuccessResult(result, http)) return result;

        var recorder = http.RequestServices.GetService(typeof(AuditRecorder)) as AuditRecorder;
        if (recorder is null || recorder.Recorded) return result;

        var (action, entity) = DeriveActionEntity(http);
        if (string.IsNullOrEmpty(entity)) return result;

        var connections = (WmsfoConnectionStrings)http.RequestServices.GetService(typeof(WmsfoConnectionStrings))!;
        var entityId = recorder.PendingEntityId ?? DeriveEntityId(http, entity);
        var before = (object?)recorder.PendingBefore;
        var after = (object?)recorder.PendingAfter;
        try
        {
            await recorder.RecordOnNewConnectionAsync(connections, recorder.PendingAction ?? action, entity, entityId, before, after, http.RequestAborted);
        }
        catch
        {
            // Fallback recording never fails the response; the endpoint's own
            // transaction has already committed. The primary contract is that
            // the endpoint records the row itself.
        }
        return result;
    }

    // Route pattern → (action, entity). A `WithAudit(action, entity)` on the
    // endpoint wins; otherwise the last path segment names the verb (activate,
    // clone, notify, ...) or, for a CRUD verb, the method chooses (POST/PATCH/
    // PUT/DELETE → create/update/update/delete). Entity is derived from the
    // first path segment after /admin/, singularised.
    private static (string action, string entity) DeriveActionEntity(HttpContext http)
    {
        var meta = http.GetEndpoint()?.Metadata.GetMetadata<AuditEndpointMetadata>();
        if (meta is not null) return (meta.Action, meta.Entity);

        var pattern = (http.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText ?? "";
        // strip /admin/
        var trimmed = pattern.StartsWith("/admin/", StringComparison.Ordinal) ? pattern.Substring("/admin/".Length) : pattern;
        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return ("", "");
        var entity = SingularEntity(segments[0]);
        // Look for a trailing verb segment (a plain word, not a parameter and not
        // the entity itself). If present, use it as the action.
        string action = MethodAction(http.Request.Method);
        // Consider possible sub-resource: /admin/{root}/{id}/{sub}/... or /admin/{root}/{sub}
        // If the last segment is a plain word and doesn't contain a route param,
        // treat it as a verb (activate, clone, revoke, current, notify, ...).
        for (var i = segments.Length - 1; i >= 1; i--)
        {
            var s = segments[i];
            if (s.StartsWith("{", StringComparison.Ordinal)) continue;
            // Treat "order", "current", "activate", etc. as verbs; also "sections",
            // "items", "messages", "years" as sub-entities when they appear
            // between params, but only pick them up here if they are at the end.
            if (i == segments.Length - 1 && !LooksLikeSubResource(s, entity))
            {
                action = s;
                break;
            }
            // If a sub-resource sits at the end, upgrade entity+action:
            if (i == segments.Length - 1 && LooksLikeSubResource(s, entity))
            {
                entity = SingularSubEntity(s, entity);
                action = MethodAction(http.Request.Method);
                break;
            }
            // If the last segment IS a parameter, the sub-resource may be at
            // segments[^2] (e.g. /admin/sections/{id}/items/{itemId}); adjust.
            if (i == segments.Length - 2 && LooksLikeSubResource(s, entity))
            {
                entity = SingularSubEntity(s, entity);
                action = MethodAction(http.Request.Method);
                break;
            }
        }
        // Special-case verbs where the segment name is a noun the code needs to
        // treat as a verb.
        if (segments.Length > 0 && string.Equals(segments[^1], "publish", StringComparison.Ordinal))
        {
            entity = "content"; action = "publish";
        }
        else if (segments.Length > 0 && string.Equals(segments[^1], "restore", StringComparison.Ordinal))
        {
            entity = "content"; action = "restore";
        }
        else if (segments.Length > 0 && string.Equals(segments[^1], "preview-token", StringComparison.Ordinal))
        {
            entity = "preview_token"; action = "create";
        }
        return (action, entity);
    }

    private static string SingularEntity(string plural) => plural switch
    {
        "api-keys" => "api_key",
        "beacons" => "beacon",
        "contact-messages" => "contact_message",
        "content" => "content",
        "cookie-types" => "cookie_type",
        "events" => "event",
        "items" => "section_item",
        "media" => "media_asset",
        "pages" => "page",
        "people" => "person",
        "routes" => "route",
        "sections" => "section",
        "settings" => "setting",
        "site-settings" => "site_settings",
        "sponsors" => "sponsor",
        "subscribers" => "subscriber",
        "audit" => "audit_log",
        _ => plural.Replace('-', '_'),
    };

    private static string SingularSubEntity(string sub, string parent) => sub switch
    {
        "messages" => "event_message",
        "years" => "sponsor_year",
        "items" => "section_item",
        "sections" => "section",
        "order" => parent + "_order",
        _ => parent,
    };

    private static bool LooksLikeSubResource(string s, string parent) => s switch
    {
        "messages" or "years" or "items" or "sections" or "order" => true,
        _ => false,
    };

    private static string MethodAction(string method) => method switch
    {
        "POST" => "create",
        "PATCH" => "update",
        "PUT" => "update",
        "DELETE" => "delete",
        _ => method.ToLowerInvariant(),
    };

    private static bool IsAdminWrite(HttpContext http)
    {
        if (http.Request.Path.Value?.StartsWith("/admin/", StringComparison.Ordinal) != true) return false;
        var m = http.Request.Method;
        return m == HttpMethods.Post || m == HttpMethods.Patch || m == HttpMethods.Put || m == HttpMethods.Delete;
    }

    private static bool IsSuccessResult(object? result, HttpContext http)
    {
        var status = http.Response.StatusCode;
        if (status == 0)
        {
            if (result is IStatusCodeHttpResult statusResult && statusResult.StatusCode is int s)
                status = s;
            else
                status = 200;
        }
        return status is >= 200 and < 300;
    }

    private static string DeriveEntityId(HttpContext http, string entity)
    {
        var routeValues = http.GetRouteData()?.Values;
        if (routeValues is null) return "";
        string? Get(string key) => routeValues.TryGetValue(key, out var v) ? v?.ToString() : null;
        return entity switch
        {
            "sponsor_year" => (Get("id") ?? "") + ":" + (Get("eventYear") ?? ""),
            "sponsors_order" or "sponsor_order" => Get("eventYear") ?? "",
            "event_message" => Get("messageId") ?? "",
            "route_from_event" => Get("eventId") ?? "",
            "setting" => Get("key") ?? "",
            _ => Get("id") ?? Get("key") ?? Get("eventYear") ?? Get("messageId") ?? "",
        };
    }
}
