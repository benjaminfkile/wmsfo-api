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
