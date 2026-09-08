using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

// contracts 4.2 beacon endpoints. Rate limits, body limits, and the serverTime
// filter come from api.md 4.0 and 5. Enroll runs sql.md 8.11 enroll; me stamps
// last_seen_at; locations goes through LocationIngest; heartbeat is sql.md 8.3;
// logs stores the body.
public static class BeaconEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapEnroll(app);
        MapMe(app);
        MapLocations(app);
        MapHeartbeat(app);
        MapLogs(app);
    }

    private static void MapEnroll(IEndpointRouteBuilder app)
    {
        app.MapPost("/beacons/enroll",
            async (EnrollRequest body, HttpContext ctx, WmsfoConnectionStrings connections, WmsfoOptions options, IServerClock clock, CancellationToken ct) =>
            {
                var token = body?.Token ?? "";
                if (!IsToken(token, Keys.EnrollmentTokenPrefix))
                {
                    RequestValidation.Throw("token", "must match ^wet_[A-Za-z0-9_-]{43}$");
                }

                var tokenHash = Keys.Hash(token);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                long beaconId = 0;
                long tokenRowId = 0;
                byte[]? cipher = null;
                string beaconName = "";
                string role = "";

                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    await using (var lookup = new NpgsqlCommand(@"
select t.id, t.beacon_id, t.key_ciphertext, b.name, b.role, b.revoked_at
from beacon_enrollment_token t
join beacon b on b.id = t.beacon_id
where t.token_hash = $1 and t.consumed_at is null and t.expires_at > now()
for update of t;", conn, tx))
                    {
                        lookup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = tokenHash });
                        await using var reader = await lookup.ExecuteReaderAsync(ct);
                        if (!await reader.ReadAsync(ct))
                        {
                            throw new ApiException(StatusCodes.Status404NotFound,
                                "enrollment_token_invalid", "unknown, used, or expired token");
                        }
                        tokenRowId = reader.GetInt64(0);
                        beaconId = reader.GetInt64(1);
                        cipher = reader.IsDBNull(2) ? null : (byte[])reader[2];
                        beaconName = reader.GetString(3);
                        role = reader.GetString(4);
                        var revokedAt = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(5);
                        if (revokedAt is not null)
                        {
                            throw new ApiException(StatusCodes.Status404NotFound,
                                "enrollment_token_invalid", "beacon revoked");
                        }
                    }

                    if (cipher is null)
                    {
                        throw new ApiException(StatusCodes.Status404NotFound,
                            "enrollment_token_invalid", "token already consumed");
                    }

                    await using (var consume = new NpgsqlCommand(
                        "update beacon_enrollment_token set consumed_at = now(), key_ciphertext = null where id = $1;", conn, tx))
                    {
                        consume.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = tokenRowId });
                        await consume.ExecuteNonQueryAsync(ct);
                    }

                    await tx.CommitAsync(ct);
                }

                var keyBytes = Convert.FromBase64String(options.EnrollmentEncryptionKey);
                var plaintextKey = Keys.Decrypt(keyBytes, cipher!);

                return Results.Ok(new EnrollResponse
                {
                    BeaconId = beaconId,
                    Name = beaconName,
                    Role = role,
                    Key = plaintextKey,
                    ApiBaseUrl = options.PublicApiBaseUrl,
                    HubUrl = options.HubUrl,
                    IngestChannel = $"{options.ServiceName}:ingest",
                    ServerTime = clock.UtcNow(),
                });
            })
            .WithTags("Beacon")
            .Accepts<EnrollRequest>("application/json")
            .Produces<EnrollResponse>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireRateLimiting(RateLimitPolicies.EnrollPerIp)
            .AddServerTime();
    }

    private static void MapMe(IEndpointRouteBuilder app)
    {
        app.MapGet("/beacons/me",
            async (HttpContext ctx, WmsfoConnectionStrings connections, WmsfoOptions options, IServerClock clock, CancellationToken ct) =>
            {
                var beaconId = BeaconAuthenticationHandler.TryGetBeaconId(ctx.User)
                    ?? throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthenticated, "unauthenticated");

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                string name;
                string role;
                bool isActive;
                await using (var cmd = new NpgsqlCommand(@"
update beacon set last_seen_at = now(), updated_at = now() where id = $1
returning name, role, is_active;", conn))
                {
                    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct))
                    {
                        throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthenticated, "unauthenticated");
                    }
                    name = reader.GetString(0);
                    role = reader.GetString(1);
                    isActive = reader.GetBoolean(2);
                }

                long? liveEventId = null;
                await using (var evt = new NpgsqlCommand(
                    "select id from event where status_id = 3;", conn))
                {
                    var r = await evt.ExecuteScalarAsync(ct);
                    if (r is long l) liveEventId = l;
                }

                return Results.Ok(new BeaconMeResponse
                {
                    BeaconId = beaconId,
                    Name = name,
                    Role = role,
                    IsActive = isActive,
                    ApiBaseUrl = options.PublicApiBaseUrl,
                    HubUrl = options.HubUrl,
                    IngestChannel = $"{options.ServiceName}:ingest",
                    LiveEventId = liveEventId,
                    ServerTime = clock.UtcNow(),
                });
            })
            .WithTags("Beacon")
            .Produces<BeaconMeResponse>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Beacon)
            .RequireRateLimiting(RateLimitPolicies.BeaconMePerBeacon)
            .AddServerTime();
    }

    private static void MapLocations(IEndpointRouteBuilder app)
    {
        app.MapPost("/locations",
            async (LocationBody body, HttpContext ctx, LocationIngest ingest, CancellationToken ct) =>
            {
                var beaconId = BeaconAuthenticationHandler.TryGetBeaconId(ctx.User)
                    ?? throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthenticated, "unauthenticated");
                var response = await ingest.HandleAsync(beaconId, body, messagePath: false, ct);
                return Results.Json(response, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("Beacon")
            .Accepts<LocationBody>("application/json")
            .Produces<LocationResponse>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Beacon)
            .RequireRateLimiting(RateLimitPolicies.LocationsPerBeacon)
            .AddServerTime();
    }

    private static void MapHeartbeat(IEndpointRouteBuilder app)
    {
        app.MapPost("/beacons/heartbeat",
            async (HttpRequest request, HttpContext ctx, WmsfoConnectionStrings connections, IServerClock clock, CancellationToken ct) =>
            {
                var beaconId = BeaconAuthenticationHandler.TryGetBeaconId(ctx.User)
                    ?? throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthenticated, "unauthenticated");

                // Read the raw body once so we can enforce the 8 KB cap ourselves
                // AND store the whole document (including tolerated nested keys)
                // as JSONB.
                var bodyBytes = await ReadBodyWithCapAsync(request, BodyLimits.Heartbeat, ct);
                HeartbeatBody parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<HeartbeatBody>(bodyBytes, Wmsfo.Api.Objects.CanonicalJson.Options)
                        ?? throw new JsonException("null body");
                }
                catch (JsonException)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest,
                        ApiErrorCodes.ValidationFailed, "malformed heartbeat body");
                }

                ValidateHeartbeat(parsed);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                bool isActive = false;
                var jsonText = System.Text.Encoding.UTF8.GetString(bodyBytes);
                await using (var upd = new NpgsqlCommand(@"
update beacon
set telemetry = $1::jsonb,
    last_heartbeat_at = now(),
    last_seen_at = now(),
    stale_since = null,
    updated_at = now()
where id = $2
returning is_active;", conn))
                {
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = jsonText });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                    var r = await upd.ExecuteScalarAsync(ct);
                    if (r is null)
                    {
                        throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthenticated, "unauthenticated");
                    }
                    isActive = (bool)r;
                }

                long? liveEventId = null;
                await using (var evt = new NpgsqlCommand(
                    "select id from event where status_id = 3;", conn))
                {
                    var r = await evt.ExecuteScalarAsync(ct);
                    if (r is long l) liveEventId = l;
                }

                var now = clock.UtcNow();
                return Results.Ok(new HeartbeatResponse
                {
                    ReceivedAt = now,
                    LiveEventId = liveEventId,
                    IsActive = isActive,
                    ServerTime = now,
                });
            })
            .WithTags("Beacon")
            .Accepts<HeartbeatBody>("application/json")
            .Produces<HeartbeatResponse>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.Heartbeat)
            .RequireAuthorization(AuthPolicies.Beacon)
            .RequireRateLimiting(RateLimitPolicies.HeartbeatPerBeacon)
            .AddServerTime();
    }

    private static void MapLogs(IEndpointRouteBuilder app)
    {
        app.MapPost("/beacons/logs",
            async (HttpRequest request, HttpContext ctx, WmsfoConnectionStrings connections, IServerClock clock, CancellationToken ct) =>
            {
                var beaconId = BeaconAuthenticationHandler.TryGetBeaconId(ctx.User)
                    ?? throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthenticated, "unauthenticated");

                var contentType = request.ContentType ?? "";
                if (!contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ApiException(StatusCodes.Status415UnsupportedMediaType,
                        ApiErrorCodes.UnsupportedMediaType, "text/plain; charset=utf-8 required");
                }

                var appVersion = request.Headers["X-App-Version"].ToString();
                if (appVersion.Length > 64)
                {
                    RequestValidation.Throw("xAppVersion", "must be 64 characters or fewer");
                }

                var bodyBytes = await ReadBodyWithCapAsync(request, BodyLimits.BeaconLog, ct);
                var body = Encoding.UTF8.GetString(bodyBytes);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(@"
insert into beacon_log (beacon_id, app_version, size_bytes, body)
values ($1, $2, $3, $4)
returning id, received_at;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = string.IsNullOrEmpty(appVersion) ? (object)DBNull.Value : appVersion });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = bodyBytes.Length });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                var id = reader.GetInt64(0);
                var receivedAt = reader.GetFieldValue<DateTimeOffset>(1);

                return Results.Json(new BeaconLogResponse
                {
                    Id = id,
                    SizeBytes = bodyBytes.Length,
                    ReceivedAt = receivedAt,
                    ServerTime = clock.UtcNow(),
                }, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("Beacon")
            .Accepts<string>("text/plain")
            .Produces<BeaconLogResponse>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.BeaconLog)
            .RequireAuthorization(AuthPolicies.BeaconAdmin)
            .RequireRateLimiting(RateLimitPolicies.BeaconLogsPerBeacon)
            .AddServerTime();
    }

    // Contracts 4.2 heartbeat rules: sentAt required rfc3339; the six group
    // objects each nullable or object; leaf ranges + enums; whole body 8 KB
    // (enforced at read time).
    public static void ValidateHeartbeat(HeartbeatBody body)
    {
        var v = new RequestValidation();
        if (body.SentAt == default) v.Field("sentAt", "required");

        void EnumField(string name, string? value, string[] allowed)
        {
            if (value is null) return;
            foreach (var a in allowed) if (a == value) return;
            v.Field(name, "value not allowed");
        }
        void Finite(string name, double? value)
        {
            if (value is double d && (double.IsNaN(d) || double.IsInfinity(d)))
                v.Field(name, "must be finite");
        }
        void Str64(string name, string? value)
        {
            if (value is not null && value.Length > 64) v.Field(name, "must be 64 characters or fewer");
        }

        if (body.Power is { } p)
        {
            EnumField("power.thermalStatus", p.ThermalStatus,
                new[] { "none", "light", "moderate", "severe", "critical", "emergency", "shutdown", "unknown" });
            Finite("power.batteryTempC", p.BatteryTempC);
        }
        if (body.Radio is { } r)
        {
            Str64("radio.networkType", r.NetworkType);
        }
        if (body.Transport is { } t)
        {
            EnumField("transport.socketState", t.SocketState,
                new[] { "connected", "connecting", "reconnecting", "disconnected" });
        }
        if (body.Process is { } proc)
        {
            EnumField("process.memoryPressure", proc.MemoryPressure,
                new[] { "normal", "moderate", "low", "critical", "unknown" });
        }
        if (body.Gps is { } gps)
        {
            Str64("gps.provider", gps.Provider);
            Finite("gps.lastFixAccuracyM", gps.LastFixAccuracyM);
        }
        if (body.Identity is { } id)
        {
            Str64("identity.deviceModel", id.DeviceModel);
            Str64("identity.androidVersion", id.AndroidVersion);
            Str64("identity.appVersion", id.AppVersion);
        }

        v.ThrowIfInvalid();
    }

    public static bool IsToken(string value, string prefix)
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

    // Enforce the endpoint's body cap even when the client does not send a Content-Length.
    // The middleware in BodyLimits.Use sets the per-request MaxRequestBodySize which
    // triggers Kestrel to throw a BadHttpRequestException at read time, but this method
    // guards it defensively.
    private static async Task<byte[]> ReadBodyWithCapAsync(HttpRequest request, long cap, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > cap)
            {
                throw new ApiException(StatusCodes.Status413PayloadTooLarge,
                    ApiErrorCodes.PayloadTooLarge, $"request body exceeds the {cap}-byte limit");
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
