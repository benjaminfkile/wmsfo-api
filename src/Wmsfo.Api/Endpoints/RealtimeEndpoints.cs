using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.Endpoints;

// api.md 12 gateway callbacks. Both endpoints:
//  * are exempt from CORS (WmsfoPipeline UseWhen)
//  * are exempt from ForwardedHeaders middleware (so this file sees the raw headers)
//  * are exempt from the serverTime filter
//  * carry DisableRateLimiting
// The forwarded-header guard runs before authentication: any X-Forwarded-For,
// X-Forwarded-Host, or X-Forwarded-Proto means 404 empty (contracts 3.5).
public static class RealtimeEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapAuthorize(app);
        MapMessage(app);
    }

    // The three headers the guard rejects on. Their presence means the request
    // was forwarded through the public proxy; the gateway's direct callback
    // client attaches none of them (contracts 3.5).
    public static readonly string[] ForwardedHeaderNames =
    {
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
    };

    public static bool HasForwardedHeader(HttpRequest request)
    {
        foreach (var h in ForwardedHeaderNames)
        {
            if (request.Headers.ContainsKey(h)) return true;
        }
        return false;
    }

    private static void MapAuthorize(IEndpointRouteBuilder app)
    {
        app.MapPost("/realtime/authorize",
            async (HttpRequest request, HttpContext ctx, WmsfoConnectionStrings connections, WmsfoOptions options, NodeCounters counters, CancellationToken ct) =>
            {
                if (HasForwardedHeader(request))
                {
                    return Results.StatusCode(StatusCodes.Status404NotFound);
                }

                RealtimeAuthorizeRequest? body;
                try
                {
                    body = await JsonSerializer.DeserializeAsync<RealtimeAuthorizeRequest>(
                        request.Body, Wmsfo.Api.Objects.CanonicalJson.Options, ct);
                }
                catch (JsonException)
                {
                    counters.IncrementAuthorize(AuthorizeBranch.IngestDeny);
                    return Results.Ok(new RealtimeAuthorizeResponse { Allow = false });
                }
                if (body is null || string.IsNullOrEmpty(body.Channel))
                {
                    counters.IncrementAuthorize(AuthorizeBranch.IngestDeny);
                    return Results.Ok(new RealtimeAuthorizeResponse { Allow = false });
                }

                var (prefix, topic) = SplitChannel(body.Channel);
                if (prefix != options.ServiceName)
                {
                    counters.IncrementAuthorize(AuthorizeBranch.IngestDeny);
                    return Results.Ok(new RealtimeAuthorizeResponse { Allow = false });
                }

                // Public topics: allow without I/O.
                if (topic is "location" or "event" or "cookies")
                {
                    counters.IncrementAuthorize(AuthorizeBranch.PublicAllow);
                    return Results.Ok(new RealtimeAuthorizeResponse { Allow = true });
                }

                if (topic == "ingest")
                {
                    var credential = body.Credential ?? "";
                    if (!BeaconEndpoints.IsToken(credential, Keys.BeaconKeyPrefix))
                    {
                        counters.IncrementAuthorize(AuthorizeBranch.IngestDeny);
                        return Results.Ok(new RealtimeAuthorizeResponse { Allow = false });
                    }
                    var hash = Keys.Hash(credential);
                    await using var conn = new NpgsqlConnection(connections.App);
                    await conn.OpenAsync(ct);
                    long beaconId;
                    int keyVersion;
                    await using (var lookup = new NpgsqlCommand(
                        "select id, key_version from beacon where key_hash = $1 and revoked_at is null;", conn))
                    {
                        lookup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = hash });
                        await using var reader = await lookup.ExecuteReaderAsync(ct);
                        if (!await reader.ReadAsync(ct))
                        {
                            counters.IncrementAuthorize(AuthorizeBranch.IngestDeny);
                            return Results.Ok(new RealtimeAuthorizeResponse { Allow = false });
                        }
                        beaconId = reader.GetInt64(0);
                        keyVersion = reader.GetInt32(1);
                    }
                    await using (var stamp = new NpgsqlCommand(
                        "update beacon set last_seen_at = now(), updated_at = now() where id = $1;", conn))
                    {
                        stamp.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                        await stamp.ExecuteNonQueryAsync(ct);
                    }
                    counters.IncrementAuthorize(AuthorizeBranch.IngestAllow);
                    return Results.Ok(new RealtimeAuthorizeResponse
                    {
                        Allow = true,
                        Identity = $"{beaconId}:{keyVersion}",
                    });
                }

                // Any other topic.
                counters.IncrementAuthorize(AuthorizeBranch.IngestDeny);
                return Results.Ok(new RealtimeAuthorizeResponse { Allow = false });
            })
            .WithTags("Realtime")
            .Accepts<RealtimeAuthorizeRequest>("application/json")
            .Produces<RealtimeAuthorizeResponse>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .DisableRateLimiting();
    }

    private static void MapMessage(IEndpointRouteBuilder app)
    {
        app.MapPost("/realtime/message",
            async (HttpRequest request, HttpContext ctx, WmsfoConnectionStrings connections, WmsfoOptions options, LocationIngest ingest, NodeCounters counters, CancellationToken ct) =>
            {
                if (HasForwardedHeader(request))
                {
                    return Results.StatusCode(StatusCodes.Status404NotFound);
                }

                RealtimeMessageRequest? body;
                try
                {
                    body = await JsonSerializer.DeserializeAsync<RealtimeMessageRequest>(
                        request.Body, Wmsfo.Api.Objects.CanonicalJson.Options, ct);
                }
                catch (JsonException)
                {
                    counters.IncrementMessage(MessageOutcome.ValidationFailed);
                    throw new ApiException(StatusCodes.Status400BadRequest,
                        ApiErrorCodes.ValidationFailed, "malformed body");
                }

                if (body is null)
                {
                    counters.IncrementMessage(MessageOutcome.ValidationFailed);
                    throw new ApiException(StatusCodes.Status400BadRequest,
                        ApiErrorCodes.ValidationFailed, "malformed body");
                }

                var expectedChannel = $"{options.ServiceName}:ingest";
                if (body.Channel != expectedChannel)
                {
                    counters.IncrementMessage(MessageOutcome.Forbidden);
                    throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "forbidden");
                }

                if (!TryParseIdentity(body.Identity, out var beaconId, out var keyVersion))
                {
                    counters.IncrementMessage(MessageOutcome.Forbidden);
                    throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "forbidden");
                }

                // sql.md 8.11: message-path identity check.
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                DateTimeOffset? revokedAt = null;
                int rowKeyVersion = -1;
                await using (var check = new NpgsqlCommand(
                    "select revoked_at, key_version from beacon where id = $1;", conn))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                    await using var reader = await check.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct))
                    {
                        counters.IncrementMessage(MessageOutcome.Forbidden);
                        throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "forbidden");
                    }
                    revokedAt = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
                    rowKeyVersion = reader.GetInt32(1);
                }
                if (revokedAt is not null || rowKeyVersion != keyVersion)
                {
                    counters.IncrementMessage(MessageOutcome.Forbidden);
                    throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "forbidden");
                }

                // Stamp last_seen_at only when the check passes.
                await using (var stamp = new NpgsqlCommand(
                    "update beacon set last_seen_at = now(), updated_at = now() where id = $1;", conn))
                {
                    stamp.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                    await stamp.ExecuteNonQueryAsync(ct);
                }

                if (body.Event == "location")
                {
                    LocationBody? locationBody;
                    try
                    {
                        locationBody = JsonSerializer.Deserialize<LocationBody>(
                            body.Data.GetRawText(), Wmsfo.Api.Objects.CanonicalJson.Options);
                    }
                    catch (JsonException)
                    {
                        counters.IncrementMessage(MessageOutcome.ValidationFailed);
                        throw new ApiException(StatusCodes.Status400BadRequest,
                            ApiErrorCodes.ValidationFailed, "malformed body");
                    }
                    if (locationBody is null)
                    {
                        counters.IncrementMessage(MessageOutcome.ValidationFailed);
                        throw new ApiException(StatusCodes.Status400BadRequest,
                            ApiErrorCodes.ValidationFailed, "malformed body");
                    }
                    var response = await ingest.HandleAsync(beaconId, locationBody, messagePath: true, ct);
                    counters.IncrementMessage(MessageOutcome.LocationOk);
                    return Results.Ok(response);
                }

                counters.IncrementMessage(MessageOutcome.ValidationFailed);
                RequestValidation.Throw("event", "must be location");
                return Results.Ok();
            })
            .WithTags("Realtime")
            .Accepts<RealtimeMessageRequest>("application/json")
            .Produces<LocationResponse>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .DisableRateLimiting();
    }

    public static (string prefix, string topic) SplitChannel(string channel)
    {
        var colon = channel.IndexOf(':');
        if (colon <= 0 || colon == channel.Length - 1) return ("", "");
        return (channel[..colon], channel[(colon + 1)..]);
    }

    public static bool TryParseIdentity(string? identity, out long beaconId, out int keyVersion)
    {
        beaconId = 0;
        keyVersion = 0;
        if (string.IsNullOrEmpty(identity)) return false;
        var colon = identity.IndexOf(':');
        if (colon <= 0 || colon == identity.Length - 1) return false;
        return long.TryParse(identity[..colon], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out beaconId)
            && int.TryParse(identity[(colon + 1)..], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out keyVersion);
    }
}
