using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Contracts.Dtos;

// POST /admin/routes body (contracts 1.4). schemaVersion is absent on upload.
public sealed class UploadRouteRequest
{
    public string Name { get; set; } = "";
    public IList<RouteUploadPoint> Points { get; set; } = new List<RouteUploadPoint>();
}

public sealed class RouteUploadPoint
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public DateTimeOffset? RecordedAt { get; set; }
}

// POST /admin/routes/from-event/{eventId}.
public sealed class RouteFromEventRequest
{
    public string Name { get; set; } = "";
}

// GET /admin/route (contracts 4.5 Routes): the site route as the newest
// published version names it and as the site settings draft names it; each
// is null when its settings name no route.
public sealed class SiteRouteResponse
{
    public PublishedSiteRouteDto? Published { get; set; }
    public DraftSiteRouteDto? Draft { get; set; }
}

// The published site route: the route row's name (null when the id names no
// row), its route map built exactly as the snapshot's `routeMap` (null when
// the object cannot be read), and `settings.route.mapConfig` (null when
// unset).
public sealed class PublishedSiteRouteDto
{
    public long RouteId { get; set; }
    public string? Name { get; set; }
    public RouteMap? RouteMap { get; set; }
    public RouteMapConfig? MapConfig { get; set; }
}

// The draft site route without the map: the route row's name (null when the
// id names no row) and the draft's `mapConfig` (null when unset).
public sealed class DraftSiteRouteDto
{
    public long RouteId { get; set; }
    public string? Name { get; set; }
    public RouteMapConfig? MapConfig { get; set; }
}
