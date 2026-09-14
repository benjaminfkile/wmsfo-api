using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Endpoints.Impact;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Endpoints;

// api.md 5b: `GET /admin/<resource>/{id}/impact` per resource. The preview
// endpoint calls `PreviewAsync` for the resource; the row-not-found case
// answers 404.
public static class AdminImpactEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapEvent(app);
        MapRoute(app);
        MapSponsor(app);
        MapCookieType(app);
        MapPage(app);
        MapMedia(app);
        MapPlace(app);
        MapQrCode(app);
        MapBeacon(app);
        MapApiKey(app);
        MapSubscriber(app);
        MapPerson(app);
        MapContactMessage(app);
    }

    private static void MapEvent(IEndpointRouteBuilder app) => Register(app,
        "/admin/events/{id:long}/impact", ApiKeyCapabilities.Events, AuthPolicies.Admin,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "event", id, ct)) return null;
            return await EventImpactQueries.PreviewAsync(conn, null, id, ct);
        });

    private static void MapRoute(IEndpointRouteBuilder app) => Register(app,
        "/admin/routes/{id:long}/impact", ApiKeyCapabilities.Routes, AuthPolicies.Admin,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "route", id, ct)) return null;
            return await RouteImpactQueries.PreviewAsync(conn, null, id, ct);
        });

    private static void MapSponsor(IEndpointRouteBuilder app) => Register(app,
        "/admin/sponsors/{id:long}/impact", ApiKeyCapabilities.Sponsors, AuthPolicies.Editor,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "sponsor", id, ct)) return null;
            return await SponsorImpactQueries.PreviewAsync(conn, null, id, ct);
        });

    private static void MapCookieType(IEndpointRouteBuilder app) => Register(app,
        "/admin/cookie-types/{id:long}/impact", ApiKeyCapabilities.CookieTypes, AuthPolicies.Admin,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "cookie_type", id, ct)) return null;
            return await CookieTypeImpactQueries.PreviewAsync(conn, null, id, ct);
        });

    private static void MapPage(IEndpointRouteBuilder app) => Register(app,
        "/admin/pages/{id:long}/impact", ApiKeyCapabilities.Pages, AuthPolicies.Editor,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "page", id, ct)) return null;
            var (impact, _) = await PageImpactQueries.PreviewAsync(conn, null, id, ct);
            return impact;
        });

    private static void MapMedia(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/media/{id}/impact",
            async (string id, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                if (!Guid.TryParse(id, out var guid))
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "not found");
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using (var check = new NpgsqlCommand(
                    "select 1 from media_asset where id = $1;", conn))
                {
                    check.Parameters.Add(new NpgsqlParameter
                    {
                        NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = guid,
                    });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull)
                        throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "not found");
                }
                var impact = await MediaImpactQueries.PreviewAsync(conn, null, guid, ct);
                return Results.Ok(impact);
            })
            .WithTags("AdminImpact")
            .Produces<DeleteImpactDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireCapability(ApiKeyCapabilities.Media)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    private static void MapPlace(IEndpointRouteBuilder app) => Register(app,
        "/admin/places/{id:long}/impact", ApiKeyCapabilities.Qr, AuthPolicies.Admin,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "place", id, ct)) return null;
            return await PlaceImpactQueries.PreviewAsync(conn, null, id, ct);
        });

    private static void MapQrCode(IEndpointRouteBuilder app) => Register(app,
        "/admin/qr-codes/{id:long}/impact", ApiKeyCapabilities.Qr, AuthPolicies.Admin,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "qr_code", id, ct)) return null;
            return await QrCodeImpactQueries.PreviewAsync(conn, null, id, ct);
        });

    private static void MapBeacon(IEndpointRouteBuilder app) => Register(app,
        "/admin/beacons/{id:long}/impact", ApiKeyCapabilities.Beacons, AuthPolicies.Admin,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "beacon", id, ct)) return null;
            return await BeaconImpactQueries.PreviewAsync(conn, null, id, ct);
        });

    private static void MapApiKey(IEndpointRouteBuilder app) => Register(app,
        "/admin/api-keys/{id:long}/impact", null, AuthPolicies.Admin,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "api_key", id, ct)) return null;
            return await ApiKeyImpactQueries.PreviewAsync(conn, null, id, ct);
        },
        denyApiKeys: true);

    private static void MapSubscriber(IEndpointRouteBuilder app) => Register(app,
        "/admin/subscribers/{id:long}/impact", ApiKeyCapabilities.Subscribers, AuthPolicies.Admin,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "subscriber", id, ct)) return null;
            return await SubscriberImpactQueries.PreviewAsync(conn, null, id, ct);
        });

    private static void MapPerson(IEndpointRouteBuilder app) => Register(app,
        "/admin/people/{id:long}/impact", ApiKeyCapabilities.People, AuthPolicies.Admin,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "person", id, ct)) return null;
            return await PersonImpactQueries.PreviewAsync(conn, null, id, ct);
        });

    private static void MapContactMessage(IEndpointRouteBuilder app) => Register(app,
        "/admin/contact-messages/{id:long}/impact", ApiKeyCapabilities.ContactMessages, AuthPolicies.Admin,
        async (id, conn, ct) =>
        {
            if (!await ExistsAsync(conn, "contact_message", id, ct)) return null;
            return await ContactMessageImpactQueries.PreviewAsync(conn, null, id, ct);
        });

    private static void Register(
        IEndpointRouteBuilder app, string path, string? capability, string policy,
        Func<long, NpgsqlConnection, CancellationToken, Task<DeleteImpactDto?>> build,
        bool denyApiKeys = false)
    {
        var builder = app.MapGet(path,
            async (long id, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var impact = await build(id, conn, ct);
                if (impact is null)
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "not found");
                return Results.Ok(impact);
            })
            .WithTags("AdminImpact")
            .Produces<DeleteImpactDto>(StatusCodes.Status200OK)
            .RequireAuthorization(policy)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
        if (capability is not null) builder.RequireCapability(capability);
        if (denyApiKeys) builder.DenyApiKeys();
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection conn, string table, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"select 1 from {table} where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = id,
        });
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is not null && r is not DBNull;
    }
}
