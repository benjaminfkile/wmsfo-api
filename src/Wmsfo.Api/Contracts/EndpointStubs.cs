using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Contracts;

// Registers every REST endpoint in contracts section 4 as a 501 stub with the
// request and response metadata OpenAPI export walks. Real handlers land in later
// tasks and must keep the same route metadata.
public static class EndpointStubs
{
    // The handler for every stub. Real endpoints replace it later.
    private static readonly Delegate NotImplemented = () => Results.StatusCode(StatusCodes.Status501NotImplemented);

    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapHealth(app);
        MapBeacons(app);
        MapPublic(app);
        MapMe(app);
        MapAdminEvents(app);
        MapAdminRoutes(app);
        MapAdminBeacons(app);
        MapAdminSponsors(app);
        MapAdminCookieTypes(app);
        MapAdminPages(app);
        MapAdminSections(app);
        MapAdminSiteSettings(app);
        MapAdminContent(app);
        MapAdminMedia(app);
        MapAdminIcons(app);
        MapAdminCookies(app);
        MapAdminSettings(app);
        MapAdminInbox(app);
        MapAdminDiagnostics(app);
        MapRealtime(app);
    }

    private static void MapHealth(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", NotImplemented)
            .WithTags("Health")
            .Produces<HealthResponse>(StatusCodes.Status200OK);
    }

    private static void MapBeacons(IEndpointRouteBuilder app)
    {
        app.MapPost("/beacons/enroll", NotImplemented)
            .WithTags("Beacon")
            .Accepts<EnrollRequest>("application/json")
            .Produces<EnrollResponse>(StatusCodes.Status200OK);

        app.MapGet("/beacons/me", NotImplemented)
            .WithTags("Beacon")
            .Produces<BeaconMeResponse>(StatusCodes.Status200OK);

        app.MapPost("/locations", NotImplemented)
            .WithTags("Beacon")
            .Accepts<LocationBody>("application/json")
            .Produces<LocationResponse>(StatusCodes.Status201Created);

        app.MapPost("/beacons/heartbeat", NotImplemented)
            .WithTags("Beacon")
            .Accepts<HeartbeatBody>("application/json")
            .Produces<HeartbeatResponse>(StatusCodes.Status200OK);

        app.MapPost("/beacons/logs", NotImplemented)
            .WithTags("Beacon")
            .Accepts<string>("text/plain")
            .Produces<BeaconLogResponse>(StatusCodes.Status201Created);
    }

    private static void MapPublic(IEndpointRouteBuilder app)
    {
        app.MapPost("/contact", NotImplemented)
            .WithTags("Public")
            .Accepts<ContactRequest>("application/json")
            .Produces<ContactResponse>(StatusCodes.Status201Created);

        app.MapPost("/subscriptions/verify", NotImplemented)
            .WithTags("Public")
            .Accepts<SubscriptionVerifyRequest>("application/json")
            .Produces<SubscriptionVerifyResponse>(StatusCodes.Status200OK);

        app.MapPost("/subscriptions/unsubscribe", NotImplemented)
            .WithTags("Public")
            .Accepts<SubscriptionUnsubscribeRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent);

        app.MapGet("/preview/document", NotImplemented)
            .WithTags("Public")
            .Produces<ContentBundleDto>(StatusCodes.Status200OK);
    }

    private static void MapMe(IEndpointRouteBuilder app)
    {
        app.MapGet("/me", NotImplemented)
            .WithTags("Me")
            .Produces<MeResponse>(StatusCodes.Status200OK);

        app.MapGet("/me/subscriptions", NotImplemented)
            .WithTags("Me")
            .Produces<ItemsResponse<SubscriptionDto>>(StatusCodes.Status200OK);

        app.MapPost("/me/subscriptions", NotImplemented)
            .WithTags("Me")
            .Accepts<CreateSubscriptionRequest>("application/json")
            .Produces<SubscriptionDto>(StatusCodes.Status201Created);

        app.MapPost("/me/subscriptions/{id:long}/resend-verification", NotImplemented)
            .WithTags("Me")
            .Produces<SubscriptionDto>(StatusCodes.Status202Accepted);

        app.MapDelete("/me/subscriptions/{id:long}", NotImplemented)
            .WithTags("Me")
            .Produces(StatusCodes.Status204NoContent);

        app.MapGet("/me/cookies", NotImplemented)
            .WithTags("Me")
            .Produces<MyCookiesResponse>(StatusCodes.Status200OK);

        app.MapPost("/cookies", NotImplemented)
            .WithTags("Me")
            .Accepts<CreateCookieRequest>("application/json")
            .Produces<CreateCookieResponse>(StatusCodes.Status201Created);
    }

    private static void MapAdminEvents(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/events", NotImplemented)
            .WithTags("AdminEvents")
            .Produces<ItemsResponse<EventDto>>(StatusCodes.Status200OK);

        app.MapPost("/admin/events", NotImplemented)
            .WithTags("AdminEvents")
            .Accepts<CreateEventRequest>("application/json")
            .Produces<EventDto>(StatusCodes.Status201Created);

        app.MapGet("/admin/events/{id:long}", NotImplemented)
            .WithTags("AdminEvents")
            .Produces<EventDto>(StatusCodes.Status200OK);

        app.MapPatch("/admin/events/{id:long}", NotImplemented)
            .WithTags("AdminEvents")
            .Accepts<PatchEventRequest>("application/json")
            .Produces<EventDto>(StatusCodes.Status200OK);

        app.MapDelete("/admin/events/{id:long}", NotImplemented)
            .WithTags("AdminEvents")
            .Produces(StatusCodes.Status204NoContent);

        app.MapPost("/admin/events/{id:long}/current", NotImplemented)
            .WithTags("AdminEvents")
            .Produces<EventDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/events/{id:long}/status", NotImplemented)
            .WithTags("AdminEvents")
            .Accepts<ChangeEventStatusRequest>("application/json")
            .Produces<EventDto>(StatusCodes.Status200OK);

        app.MapGet("/admin/events/{id:long}/status-history", NotImplemented)
            .WithTags("AdminEvents")
            .Produces<ItemsResponse<StatusHistoryDto>>(StatusCodes.Status200OK);

        app.MapGet("/admin/events/{id:long}/messages", NotImplemented)
            .WithTags("AdminEvents")
            .Produces<ItemsResponse<EventMessageDto>>(StatusCodes.Status200OK);

        app.MapPost("/admin/events/{id:long}/messages", NotImplemented)
            .WithTags("AdminEvents")
            .Accepts<CreateEventMessageRequest>("application/json")
            .Produces<EventMessageDto>(StatusCodes.Status201Created);

        app.MapPatch("/admin/events/{id:long}/messages/{messageId:long}", NotImplemented)
            .WithTags("AdminEvents")
            .Accepts<PatchEventMessageRequest>("application/json")
            .Produces<EventMessageDto>(StatusCodes.Status200OK);

        app.MapDelete("/admin/events/{id:long}/messages/{messageId:long}", NotImplemented)
            .WithTags("AdminEvents")
            .Produces(StatusCodes.Status204NoContent);

        app.MapGet("/admin/events/{id:long}/locations", NotImplemented)
            .WithTags("AdminEvents")
            .Produces<PageResponse<LocationRowDto>>(StatusCodes.Status200OK);

        app.MapGet("/admin/events/{id:long}/cookies", NotImplemented)
            .WithTags("AdminEvents")
            .Produces<PageResponse<CookieAdminDto>>(StatusCodes.Status200OK);
    }

    private static void MapAdminRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/routes", NotImplemented)
            .WithTags("AdminRoutes")
            .Produces<ItemsResponse<RouteDto>>(StatusCodes.Status200OK);

        app.MapGet("/admin/routes/{id:long}", NotImplemented)
            .WithTags("AdminRoutes")
            .Produces<RouteDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/routes", NotImplemented)
            .WithTags("AdminRoutes")
            .Accepts<UploadRouteRequest>("application/json")
            .Produces<RouteDto>(StatusCodes.Status201Created);

        app.MapPost("/admin/routes/from-event/{eventId:long}", NotImplemented)
            .WithTags("AdminRoutes")
            .Accepts<RouteFromEventRequest>("application/json")
            .Produces<RouteDto>(StatusCodes.Status201Created);

        app.MapDelete("/admin/routes/{id:long}", NotImplemented)
            .WithTags("AdminRoutes")
            .Produces(StatusCodes.Status204NoContent);
    }

    private static void MapAdminBeacons(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/beacons", NotImplemented)
            .WithTags("AdminBeacons")
            .Produces<BeaconsListResponse>(StatusCodes.Status200OK);

        app.MapGet("/admin/beacons/{id:long}", NotImplemented)
            .WithTags("AdminBeacons")
            .Produces<BeaconDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/beacons", NotImplemented)
            .WithTags("AdminBeacons")
            .Accepts<CreateBeaconRequest>("application/json")
            .Produces<BeaconWithKeyResponse>(StatusCodes.Status201Created);

        app.MapPatch("/admin/beacons/{id:long}", NotImplemented)
            .WithTags("AdminBeacons")
            .Accepts<PatchBeaconRequest>("application/json")
            .Produces<BeaconDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/beacons/{id:long}/activate", NotImplemented)
            .WithTags("AdminBeacons")
            .Produces<BeaconDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/beacons/{id:long}/deactivate", NotImplemented)
            .WithTags("AdminBeacons")
            .Produces<BeaconDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/beacons/{id:long}/rotate", NotImplemented)
            .WithTags("AdminBeacons")
            .Produces<BeaconWithKeyResponse>(StatusCodes.Status200OK);

        app.MapPost("/admin/beacons/{id:long}/revoke", NotImplemented)
            .WithTags("AdminBeacons")
            .Produces<BeaconDto>(StatusCodes.Status200OK);

        app.MapGet("/admin/beacons/{id:long}/logs", NotImplemented)
            .WithTags("AdminBeacons")
            .Produces<ItemsResponse<BeaconLogDto>>(StatusCodes.Status200OK);

        app.MapGet("/admin/beacons/{id:long}/logs/{logId:long}", NotImplemented)
            .WithTags("AdminBeacons")
            .Produces<string>(StatusCodes.Status200OK, "text/plain");
    }

    private static void MapAdminSponsors(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/sponsors", NotImplemented)
            .WithTags("AdminSponsors")
            .Produces<ItemsResponse<SponsorDto>>(StatusCodes.Status200OK);

        app.MapGet("/admin/sponsors/{id:long}", NotImplemented)
            .WithTags("AdminSponsors")
            .Produces<SponsorDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/sponsors", NotImplemented)
            .WithTags("AdminSponsors")
            .Accepts<CreateSponsorRequest>("application/json")
            .Produces<SponsorDto>(StatusCodes.Status201Created);

        app.MapPatch("/admin/sponsors/{id:long}", NotImplemented)
            .WithTags("AdminSponsors")
            .Accepts<PatchSponsorRequest>("application/json")
            .Produces<SponsorDto>(StatusCodes.Status200OK);

        app.MapDelete("/admin/sponsors/{id:long}", NotImplemented)
            .WithTags("AdminSponsors")
            .Produces(StatusCodes.Status204NoContent);

        app.MapPut("/admin/sponsors/{id:long}/years/{eventYear:int}", NotImplemented)
            .WithTags("AdminSponsors")
            .Accepts<UpsertSponsorYearRequest>("application/json")
            .Produces<SponsorDto>(StatusCodes.Status200OK);

        app.MapDelete("/admin/sponsors/{id:long}/years/{eventYear:int}", NotImplemented)
            .WithTags("AdminSponsors")
            .Produces(StatusCodes.Status204NoContent);
    }

    private static void MapAdminCookieTypes(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/cookie-types", NotImplemented)
            .WithTags("AdminCookieTypes")
            .Produces<ItemsResponse<CookieTypeDto>>(StatusCodes.Status200OK);

        app.MapPost("/admin/cookie-types", NotImplemented)
            .WithTags("AdminCookieTypes")
            .Accepts<CreateCookieTypeRequest>("application/json")
            .Produces<CookieTypeDto>(StatusCodes.Status201Created);

        app.MapPatch("/admin/cookie-types/{id:long}", NotImplemented)
            .WithTags("AdminCookieTypes")
            .Accepts<PatchCookieTypeRequest>("application/json")
            .Produces<CookieTypeDto>(StatusCodes.Status200OK);
    }

    private static void MapAdminPages(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/pages", NotImplemented)
            .WithTags("AdminPages")
            .Produces<ItemsResponse<PageAdminDto>>(StatusCodes.Status200OK);

        app.MapPost("/admin/pages", NotImplemented)
            .WithTags("AdminPages")
            .Accepts<CreatePageRequest>("application/json")
            .Produces<PageAdminDto>(StatusCodes.Status201Created);

        app.MapGet("/admin/pages/{id:long}", NotImplemented)
            .WithTags("AdminPages")
            .Produces<PageDetailDto>(StatusCodes.Status200OK);

        app.MapPatch("/admin/pages/{id:long}", NotImplemented)
            .WithTags("AdminPages")
            .Accepts<PatchPageRequest>("application/json")
            .Produces<PageAdminDto>(StatusCodes.Status200OK);

        app.MapDelete("/admin/pages/{id:long}", NotImplemented)
            .WithTags("AdminPages")
            .Produces(StatusCodes.Status204NoContent);

        app.MapPut("/admin/pages/order", NotImplemented)
            .WithTags("AdminPages")
            .Accepts<PageOrderRequest>("application/json")
            .Produces<ItemsResponse<PageAdminDto>>(StatusCodes.Status200OK);
    }

    private static void MapAdminSections(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/content/kinds", NotImplemented)
            .WithTags("AdminSections")
            .Produces<ItemsResponse<KindInfoDto>>(StatusCodes.Status200OK);

        app.MapPost("/admin/pages/{id:long}/sections", NotImplemented)
            .WithTags("AdminSections")
            .Accepts<CreateSectionRequest>("application/json")
            .Produces<SectionAdminDto>(StatusCodes.Status201Created);

        app.MapPatch("/admin/sections/{id:long}", NotImplemented)
            .WithTags("AdminSections")
            .Accepts<PatchSectionRequest>("application/json")
            .Produces<SectionAdminDto>(StatusCodes.Status200OK);

        app.MapDelete("/admin/sections/{id:long}", NotImplemented)
            .WithTags("AdminSections")
            .Produces(StatusCodes.Status204NoContent);

        app.MapPost("/admin/sections/{id:long}/duplicate", NotImplemented)
            .WithTags("AdminSections")
            .Produces<SectionAdminDto>(StatusCodes.Status201Created);

        app.MapPost("/admin/sections/{id:long}/move", NotImplemented)
            .WithTags("AdminSections")
            .Accepts<MoveSectionRequest>("application/json")
            .Produces<SectionAdminDto>(StatusCodes.Status200OK);

        app.MapPut("/admin/pages/{id:long}/sections/order", NotImplemented)
            .WithTags("AdminSections")
            .Accepts<SectionOrderRequest>("application/json")
            .Produces<PageDetailDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/sections/{id:long}/items", NotImplemented)
            .WithTags("AdminSections")
            .Accepts<CreateSectionItemRequest>("application/json")
            .Produces<SectionItemAdminDto>(StatusCodes.Status201Created);

        app.MapPatch("/admin/items/{id:long}", NotImplemented)
            .WithTags("AdminSections")
            .Accepts<PatchSectionItemRequest>("application/json")
            .Produces<SectionItemAdminDto>(StatusCodes.Status200OK);

        app.MapDelete("/admin/items/{id:long}", NotImplemented)
            .WithTags("AdminSections")
            .Produces(StatusCodes.Status204NoContent);

        app.MapPut("/admin/sections/{id:long}/items/order", NotImplemented)
            .WithTags("AdminSections")
            .Accepts<ItemOrderRequest>("application/json")
            .Produces<SectionAdminDto>(StatusCodes.Status200OK);
    }

    private static void MapAdminSiteSettings(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/site-settings", NotImplemented)
            .WithTags("AdminSiteSettings")
            .Produces<SiteSettingsDraftDto>(StatusCodes.Status200OK);

        app.MapPut("/admin/site-settings", NotImplemented)
            .WithTags("AdminSiteSettings")
            .Accepts<SiteSettingsUpdateRequest>("application/json")
            .Produces<SiteSettingsDraftDto>(StatusCodes.Status200OK);
    }

    private static void MapAdminContent(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/content/status", NotImplemented)
            .WithTags("AdminContent")
            .Produces<ContentStatusDto>(StatusCodes.Status200OK);

        app.MapGet("/admin/content/draft", NotImplemented)
            .WithTags("AdminContent")
            .Produces<ContentBundleDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/content/publish", NotImplemented)
            .WithTags("AdminContent")
            .Accepts<PublishContentRequest>("application/json")
            .Produces<ContentVersionInfoDto>(StatusCodes.Status201Created);

        app.MapGet("/admin/content/versions", NotImplemented)
            .WithTags("AdminContent")
            .Produces<ItemsResponse<ContentVersionInfoDto>>(StatusCodes.Status200OK);

        app.MapGet("/admin/content/versions/{id:long}", NotImplemented)
            .WithTags("AdminContent")
            .Produces<ContentVersionDetailDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/content/versions/{id:long}/restore", NotImplemented)
            .WithTags("AdminContent")
            .Produces<ContentStatusDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/content/preview-token", NotImplemented)
            .WithTags("AdminContent")
            .Produces<PreviewTokenDto>(StatusCodes.Status201Created);
    }

    private static void MapAdminMedia(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/media", NotImplemented)
            .WithTags("AdminMedia")
            .Produces<PageResponse<MediaAssetDto>>(StatusCodes.Status200OK);

        app.MapPost("/admin/media/upload-url", NotImplemented)
            .WithTags("AdminMedia")
            .Accepts<MediaUploadUrlRequest>("application/json")
            .Produces<UploadTicketDto>(StatusCodes.Status201Created);

        app.MapPost("/admin/media/{id}/confirm", NotImplemented)
            .WithTags("AdminMedia")
            .Produces<MediaAssetDto>(StatusCodes.Status200OK);

        app.MapGet("/admin/media/{id}", NotImplemented)
            .WithTags("AdminMedia")
            .Produces<MediaAssetDto>(StatusCodes.Status200OK);

        app.MapGet("/admin/media/{id}/usage", NotImplemented)
            .WithTags("AdminMedia")
            .Produces<MediaUsageDto>(StatusCodes.Status200OK);

        app.MapPatch("/admin/media/{id}", NotImplemented)
            .WithTags("AdminMedia")
            .Accepts<MediaPatchRequest>("application/json")
            .Produces<MediaAssetDto>(StatusCodes.Status200OK);

        app.MapDelete("/admin/media/{id}", NotImplemented)
            .WithTags("AdminMedia")
            .Produces(StatusCodes.Status204NoContent);
    }

    private static void MapAdminIcons(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/icons", NotImplemented)
            .WithTags("AdminIcons")
            .Produces<ItemsResponse<IconInfoDto>>(StatusCodes.Status200OK);
    }

    private static void MapAdminCookies(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/cookies/{id:long}/hide", NotImplemented)
            .WithTags("AdminCookies")
            .Produces<CookieAdminDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/cookies/{id:long}/unhide", NotImplemented)
            .WithTags("AdminCookies")
            .Produces<CookieAdminDto>(StatusCodes.Status200OK);

        app.MapDelete("/admin/cookies/{id:long}", NotImplemented)
            .WithTags("AdminCookies")
            .Produces(StatusCodes.Status204NoContent);
    }

    private static void MapAdminSettings(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/settings", NotImplemented)
            .WithTags("AdminSettings")
            .Produces<ItemsResponse<SettingDto>>(StatusCodes.Status200OK);

        app.MapPut("/admin/settings/{key}", NotImplemented)
            .WithTags("AdminSettings")
            .Accepts<SettingUpdateRequest>("application/json")
            .Produces<SettingDto>(StatusCodes.Status200OK);
    }

    private static void MapAdminInbox(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/contact-messages", NotImplemented)
            .WithTags("AdminInbox")
            .Produces<PageResponse<ContactMessageDto>>(StatusCodes.Status200OK);

        app.MapDelete("/admin/contact-messages/{id:long}", NotImplemented)
            .WithTags("AdminInbox")
            .Produces(StatusCodes.Status204NoContent);

        app.MapGet("/admin/subscribers", NotImplemented)
            .WithTags("AdminInbox")
            .Produces<PageResponse<SubscriberAdminDto>>(StatusCodes.Status200OK);

        app.MapGet("/admin/subscribers/summary", NotImplemented)
            .WithTags("AdminInbox")
            .Produces<SubscribersSummaryResponse>(StatusCodes.Status200OK);

        app.MapDelete("/admin/subscribers/{id:long}", NotImplemented)
            .WithTags("AdminInbox")
            .Produces(StatusCodes.Status204NoContent);

        app.MapGet("/admin/people", NotImplemented)
            .WithTags("AdminInbox")
            .Produces<PageResponse<PersonWithCookieCountDto>>(StatusCodes.Status200OK);

        app.MapDelete("/admin/people/{id:long}", NotImplemented)
            .WithTags("AdminInbox")
            .Produces(StatusCodes.Status204NoContent);
    }

    private static void MapAdminDiagnostics(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/snapshot", NotImplemented)
            .WithTags("AdminDiagnostics")
            .Produces<SnapshotInfoDto>(StatusCodes.Status200OK);

        app.MapPost("/admin/snapshot/rebuild", NotImplemented)
            .WithTags("AdminDiagnostics")
            .Produces<SnapshotInfoDto>(StatusCodes.Status200OK);

        app.MapGet("/admin/live", NotImplemented)
            .WithTags("AdminDiagnostics")
            .Produces<AdminLiveResponse>(StatusCodes.Status200OK);

        app.MapPost("/admin/live/republish", NotImplemented)
            .WithTags("AdminDiagnostics")
            .Produces<LiveObject>(StatusCodes.Status200OK);
    }

    private static void MapRealtime(IEndpointRouteBuilder app)
    {
        app.MapPost("/realtime/authorize", NotImplemented)
            .WithTags("Realtime")
            .Accepts<RealtimeAuthorizeRequest>("application/json")
            .Produces<RealtimeAuthorizeResponse>(StatusCodes.Status200OK);

        app.MapPost("/realtime/message", NotImplemented)
            .WithTags("Realtime")
            .Accepts<RealtimeMessageRequest>("application/json")
            .Produces<LocationResponse>(StatusCodes.Status200OK);
    }
}
