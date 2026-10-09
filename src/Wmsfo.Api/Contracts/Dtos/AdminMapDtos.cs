using System.Text.Json.Serialization;

namespace Wmsfo.Api.Contracts.Dtos;

// One tracker map (contracts 4.0 TrackerMap): GET /admin/maps and every map
// write's answer; the confirm writes it as maps/{packageKey}/manifest.json.
public sealed class TrackerMapDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    // sha256 of the canonical { bbox, minZoom, maxZoom, terrainMaxZoom }.
    public string PackageKey { get; set; } = "";
    // maps/{packageKey}, or basemap for the seeded map.
    public string Prefix { get; set; } = "";
    public Bbox Bbox { get; set; } = new();
    public int MinZoom { get; set; }
    public int MaxZoom { get; set; }
    // Null: the package has no terrain archive.
    public int? TerrainMaxZoom { get; set; }
    // The archives' lengths, recorded at confirm; null while pending.
    public long? TilesBytes { get; set; }
    public long? TerrainBytes { get; set; }
    // The basemap build date the vector tiles came from, YYYY-MM-DD.
    public string? SourceBuild { get; set; }
    // pending or ready.
    public string State { get; set; } = "";
    public DateTimeOffset? BuiltAt { get; set; }
    // The absolute CDN URLs the snapshot carries (contracts 1.3 event.trackerMap).
    public string TilesUrl { get; set; } = "";
    public string? TerrainUrl { get; set; }
    // Events whose map it is.
    public int EventCount { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public AuditStampDto? Audit { get; set; }
}

// POST /admin/maps: the package the tile CLI built. terrainMaxZoom null or
// absent declares no terrain archive; sourceBuild is a YYYY-MM-DD date or null.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateTrackerMapRequest
{
    public string Name { get; set; } = "";
    public Bbox Bbox { get; set; } = new();
    public int MinZoom { get; set; }
    public int MaxZoom { get; set; }
    public int? TerrainMaxZoom { get; set; }
    public string? SourceBuild { get; set; }
}

// POST /admin/maps answer (contracts 4.0 TrackerMapUpload): the row, its key,
// and the open upload of each declared archive.
public sealed class TrackerMapUploadDto
{
    public long Id { get; set; }
    public string PackageKey { get; set; } = "";
    public TrackerMapUploadsDto Uploads { get; set; } = new();
}

public sealed class TrackerMapUploadsDto
{
    public TrackerMapUploadIdDto Tiles { get; set; } = new();
    // Null when the package declares no terrain archive.
    public TrackerMapUploadIdDto? Terrain { get; set; }
}

public sealed class TrackerMapUploadIdDto
{
    public string UploadId { get; set; } = "";
}

// POST /admin/maps/{id}/parts: the archive (tiles or terrain) and 1 to 100
// distinct part numbers from 1 to 10,000.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TrackerMapPartsRequest
{
    public string File { get; set; } = "";
    public IList<int> PartNumbers { get; set; } = new List<int>();
}

// POST /admin/maps/{id}/parts answer (contracts 4.0 TrackerMapParts): one
// presigned UploadPart URL per number, each valid until expiresAt.
public sealed class TrackerMapPartsDto
{
    public string File { get; set; } = "";
    public IList<TrackerMapPartUrlDto> Parts { get; set; } = new List<TrackerMapPartUrlDto>();
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class TrackerMapPartUrlDto
{
    public int PartNumber { get; set; }
    public string Url { get; set; } = "";
}

// POST /admin/maps/{id}/complete: every part of the archive in order with the
// etag its upload answered.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TrackerMapCompleteRequest
{
    public string File { get; set; } = "";
    public IList<TrackerMapCompletePartDto> Parts { get; set; } = new List<TrackerMapCompletePartDto>();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TrackerMapCompletePartDto
{
    public int PartNumber { get; set; }
    public string Etag { get; set; } = "";
}

// PATCH /admin/maps/{id}: the name only; any other field is 400.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PatchTrackerMapRequest
{
    public string Name { get; set; } = "";
}

// DELETE /admin/maps/{id}: an optional body naming another ready map whose
// package contains every referencing event's box; those events take it.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DeleteTrackerMapRequest
{
    public long? ReplacementId { get; set; }
}
