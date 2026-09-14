using System.Text.Json.Serialization;

namespace Wmsfo.Api.Contracts.Dtos;

// contracts 4.5a QR codes and places. The shapes below are the wire shapes for
// /admin/qr-codes/*, /admin/places/*, and the snapshot's `qrCodes` block.

public sealed class OpensDto
{
    // "home", "page", or "url".
    [JsonPropertyOrder(0)] public string Kind { get; set; } = "home";
    // Present when kind is "page".
    [JsonPropertyOrder(1)] public long? PageId { get; set; }
    [JsonPropertyOrder(2)] public string? Slug { get; set; }
    // Present when kind is "url".
    [JsonPropertyOrder(3)] public string? Url { get; set; }
}

public sealed class QrAttachmentSummaryDto
{
    public long Id { get; set; }
    public long PlaceId { get; set; }
    public IList<string> PlacePath { get; set; } = new List<string>();
    public DateTimeOffset Since { get; set; }
}

public sealed class QrScanSummaryDto
{
    public int People { get; set; }
    public int Flagged { get; set; }
    public DateTimeOffset? LastScanAt { get; set; }
}

public class QrCodeDto
{
    public long Id { get; set; }
    public string Tag { get; set; } = "";
    public int BatchNo { get; set; }
    public DateTimeOffset PrintedAt { get; set; }
    public bool Active { get; set; }
    public string Note { get; set; } = "";
    public long? OpensPageId { get; set; }
    public string? ForwardUrl { get; set; }
    public OpensDto Opens { get; set; } = new();
    // "code", "place", or "home".
    public string OpensSource { get; set; } = "home";
    public QrAttachmentSummaryDto? Attachment { get; set; }
    public QrScanSummaryDto Scans { get; set; } = new();
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public AuditStampDto? Audit { get; set; }
}

public sealed class QrHistoryEntryDto
{
    public long AttachmentId { get; set; }
    public long? PlaceId { get; set; }   // null once the place was deleted; placePath is then empty
    public IList<string> PlacePath { get; set; } = new List<string>();
    public DateTimeOffset FromAt { get; set; }
    public DateTimeOffset? ToAt { get; set; }
    public int People { get; set; }
    public int EarlyScans { get; set; }
}

public sealed class QrDailyDto
{
    // ISO date (YYYY-MM-DD).
    public string Day { get; set; } = "";
    public int People { get; set; }
}

public sealed class QrCodeDetailDto : QrCodeDto
{
    public IList<QrHistoryEntryDto> History { get; set; } = new List<QrHistoryEntryDto>();
    public IList<QrDailyDto> Daily { get; set; } = new List<QrDailyDto>();
}

public sealed class PlaceLocationDto
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double? AccuracyM { get; set; }
    public string Source { get; set; } = "";
    public string PinnedBy { get; set; } = "";
    public DateTimeOffset PinnedAt { get; set; }
}

public sealed class PlacePinResolvedDto
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public long FromPlaceId { get; set; }
}

public sealed class PlaceCodeRefDto
{
    public long Id { get; set; }
    public string Tag { get; set; } = "";
}

public sealed class PlaceScansDto
{
    public int People { get; set; }
}

public sealed class PlaceDto
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public IList<string> Path { get; set; } = new List<string>();
    public long? OpensPageId { get; set; }
    public string? ForwardUrl { get; set; }
    public OpensDto Opens { get; set; } = new();
    // "place", "ancestor", or "home".
    public string OpensSource { get; set; } = "home";
    public PlaceLocationDto? Location { get; set; }
    public PlacePinResolvedDto? Pin { get; set; }
    public IList<PlaceCodeRefDto> Codes { get; set; } = new List<PlaceCodeRefDto>();
    public PlaceScansDto Scans { get; set; } = new();
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public AuditStampDto? Audit { get; set; }
}

public sealed class PlacePinCodeRefDto
{
    public string Tag { get; set; } = "";
    public string PlaceName { get; set; } = "";
    public int People { get; set; }
}

public sealed class PlacePinDto
{
    public long PlaceId { get; set; }
    public string Name { get; set; } = "";
    public IList<string> Path { get; set; } = new List<string>();
    public double Lat { get; set; }
    public double Lng { get; set; }
    public int People { get; set; }
    public IList<PlacePinCodeRefDto> Codes { get; set; } = new List<PlacePinCodeRefDto>();
}

public sealed class PlacePinsResponse
{
    public IList<PlacePinDto> Items { get; set; } = new List<PlacePinDto>();
    public int Unpinned { get; set; }
    public int Unattached { get; set; }
}

public sealed class MintQrCodesRequest
{
    public int Count { get; set; }
}

// PatchQrCodeRequest is parsed from raw JSON in the endpoint so `null` and
// "absent" are distinguishable (contracts 4.5a: opensPageId=null means "same as
// the place", but a missing field means "leave alone").
public sealed class PatchQrCodeRequest
{
    public long? OpensPageId { get; set; }
    public bool HasOpensPageId { get; set; }
    public string? ForwardUrl { get; set; }
    public bool HasForwardUrl { get; set; }
    public string? Note { get; set; }
    public bool? Active { get; set; }
}

public sealed class AttachQrCodeRequest
{
    public long PlaceId { get; set; }
}

public sealed class CreatePlaceRequest
{
    public long? ParentId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public long? OpensPageId { get; set; }
    public string? ForwardUrl { get; set; }
}

public sealed class PatchPlaceRequest
{
    public long? ParentId { get; set; }
    public bool HasParentId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public long? OpensPageId { get; set; }
    public bool HasOpensPageId { get; set; }
    public string? ForwardUrl { get; set; }
    public bool HasForwardUrl { get; set; }
}

public sealed class PutPlaceLocationRequest
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double? AccuracyM { get; set; }
    public string Source { get; set; } = "";
}

public sealed class QrScanRequest
{
    public string? Referrer { get; set; }
}

// contracts 1.3 snapshot: `qrCodes` map from tag to opens.
public sealed class SnapshotQrCodeEntry
{
    [JsonPropertyOrder(0)] public string? PageSlug { get; set; }
    [JsonPropertyOrder(1)] public string? ForwardUrl { get; set; }
}
