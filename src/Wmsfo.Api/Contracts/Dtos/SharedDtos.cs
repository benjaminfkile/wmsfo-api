using System.Text.Json;
using System.Text.Json.Serialization;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Contracts.Dtos;

// Shared resource shapes from contracts 4.0. All camelCase, all timestamps rfc3339.
// These types are for API responses (unknown-field handling is a request-side concern).

public sealed class EventDto
{
    public long Id { get; set; }
    public int Year { get; set; }
    public string Name { get; set; } = "";
    public int StatusId { get; set; }
    public bool IsCurrent { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? WentLiveAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int FundsPercent { get; set; }
    public long? RouteId { get; set; }
    public string? RouteUrl { get; set; }
    public string? RouteImageMediaId { get; set; }
    public MediaAssetDto? RouteImage { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EventMessageDto
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public string Body { get; set; } = "";
    public DateTimeOffset? EventTime { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StatusHistoryDto
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public int? FromStatusId { get; set; }
    public int ToStatusId { get; set; }
    public string ChangedBy { get; set; } = "";
    public DateTimeOffset ChangedAt { get; set; }
}

public sealed class RouteDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string S3Key { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int PointCount { get; set; }
    public string UploadedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BeaconDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Role { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastLocationAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? StaleSince { get; set; }
    public HeartbeatBody? Telemetry { get; set; }
    public bool? HubConnected { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EnrollmentDto
{
    public string Token { get; set; } = "";
    public string Url { get; set; } = "";
    public string QrPngDataUrl { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class BeaconLogDto
{
    public long Id { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? AppVersion { get; set; }
    public int SizeBytes { get; set; }
}

public sealed class SponsorDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? ContactPerson { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? FbUrl { get; set; }
    public string? IgUrl { get; set; }
    public string? LogoMediaId { get; set; }
    public MediaAssetDto? Logo { get; set; }
    public IList<SponsorYearDto> Years { get; set; } = new List<SponsorYearDto>();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SponsorYearDto
{
    public int EventYear { get; set; }
    [JsonConverter(typeof(NullableAmountDonatedDecimalConverter))]
    public decimal? AmountDonated { get; set; }
    public bool Active { get; set; }
    public bool CanAdvertise { get; set; }
    public bool Anonymous { get; set; }
    public int? PinnedPosition { get; set; }
    public int? LingerMsOverride { get; set; }
    public int LingerMs { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
}

public sealed class SponsorOrderRow
{
    public long SponsorId { get; set; }
    public string Name { get; set; } = "";
    public int? PinnedPosition { get; set; }
    [JsonConverter(typeof(NullableAmountDonatedDecimalConverter))]
    public decimal? AmountDonated { get; set; }
    public int LingerMs { get; set; }
    public int? LingerMsOverride { get; set; }
    public bool InSnapshot { get; set; }
}

public sealed class SponsorOrderRequest
{
    public IList<long> PinnedSponsorIds { get; set; } = new List<long>();
}

public sealed class CookieTypeDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public IconValue? Icon { get; set; }
    public int Sort { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CookieAdminDto
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public long PersonId { get; set; }
    public string PersonEmail { get; set; } = "";
    public long CookieTypeId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset LeftAt { get; set; }
    public DateTimeOffset? HiddenAt { get; set; }
    public string? HiddenBy { get; set; }
}

public sealed class SubscriptionDto
{
    public long Id { get; set; }
    public string Channel { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? UnsubscribedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SubscriberAdminDto
{
    public long Id { get; set; }
    public string Channel { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? UnsubscribedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long PersonId { get; set; }
    public string PersonEmail { get; set; } = "";
}

public sealed class PersonDto
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class PersonWithCookieCountDto
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int CookieCount { get; set; }
}

public sealed class ContactMessageDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Body { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SettingDto
{
    public string Key { get; set; } = "";
    public JsonElement Value { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class SnapshotInfoDto
{
    public long Version { get; set; }
    public string Url { get; set; } = "";
    public string S3Key { get; set; } = "";
    public DateTimeOffset BuiltAt { get; set; }
}

public sealed class LocationRowDto
{
    public long Seq { get; set; }
    public long BeaconId { get; set; }
    public bool Published { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double? SpeedMps { get; set; }
    public double? AltitudeM { get; set; }
    public double? HeadingDeg { get; set; }
    public double? AccuracyM { get; set; }
}

public sealed class PageResponse<T>
{
    public IList<T> Items { get; set; } = new List<T>();
    public string? NextCursor { get; set; }
}

public sealed class ItemsResponse<T>
{
    public IList<T> Items { get; set; } = new List<T>();
}

public sealed class ProblemDto
{
    public string Path { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ProblemRefDto
{
    public string Path { get; set; } = "";
    public string Message { get; set; } = "";
    public long? PageId { get; set; }
    public long? SectionId { get; set; }
    public long? ItemId { get; set; }
}

public sealed class PresentationDto
{
    public string Width { get; set; } = "";
    public string Align { get; set; } = "";
    public JsonElement Background { get; set; }
    public string Spacing { get; set; } = "";
    public IconValue? IconBefore { get; set; }
    public IconValue? IconAfter { get; set; }
    public string? Anchor { get; set; }
}

public sealed class PageAdminDto
{
    public long Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? NavLabel { get; set; }
    public int NavPosition { get; set; }
    public bool IsHidden { get; set; }
    public string Role { get; set; } = "";
    public int SectionCount { get; set; }
    public int ProblemCount { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SectionItemAdminDto
{
    public long Id { get; set; }
    public long SectionId { get; set; }
    public int Position { get; set; }
    public bool IsHidden { get; set; }
    public JsonElement Data { get; set; }
    public IList<ProblemDto> Problems { get; set; } = new List<ProblemDto>();
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SectionAdminDto
{
    public long Id { get; set; }
    public long PageId { get; set; }
    public string Kind { get; set; } = "";
    public int Position { get; set; }
    public bool IsHidden { get; set; }
    public JsonElement Data { get; set; }
    public PresentationDto Presentation { get; set; } = new();
    public IList<SectionItemAdminDto> Items { get; set; } = new List<SectionItemAdminDto>();
    public IList<ProblemDto> Problems { get; set; } = new List<ProblemDto>();
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PageDetailDto
{
    public long Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? NavLabel { get; set; }
    public int NavPosition { get; set; }
    public bool IsHidden { get; set; }
    public string Role { get; set; } = "";
    public int SectionCount { get; set; }
    public int ProblemCount { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public IList<SectionAdminDto> Sections { get; set; } = new List<SectionAdminDto>();
}

public sealed class SiteSettingsDraftDto
{
    public JsonElement Data { get; set; }
    public IList<ProblemDto> Problems { get; set; } = new List<ProblemDto>();
    public string? UpdatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class KindInfoDto
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Live { get; set; }
    public bool HasItems { get; set; }
    public IList<string>? AllowedRoles { get; set; }
    public JsonElement Schema { get; set; }
    public JsonElement? ItemSchema { get; set; }
    public JsonElement Defaults { get; set; }
    public JsonElement? ItemDefaults { get; set; }
}

public sealed class IconInfoDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public IList<string> Tags { get; set; } = new List<string>();
    public string Url { get; set; } = "";
}

public sealed class MediaAssetDto
{
    public string Id { get; set; } = "";
    public string Filename { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "";
    public long? SizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Sha256 { get; set; }
    public string Alt { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public IDictionary<string, string> Variants { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    public string UploadedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? UnreferencedSince { get; set; }
    public DateTimeOffset? OrphanedAt { get; set; }
}

public sealed class UploadTicketDto
{
    public MediaAssetDto Media { get; set; } = new();
    public string UploadUrl { get; set; } = "";
    public string Method { get; set; } = "PUT";
    public IDictionary<string, string> Headers { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class MediaUsageDto
{
    public IList<MediaUsagePageRef> DraftPages { get; set; } = new List<MediaUsagePageRef>();
    public int VersionCount { get; set; }
    public IList<MediaUsageSponsorRef> Sponsors { get; set; } = new List<MediaUsageSponsorRef>();
    public IList<MediaUsageCookieTypeRef> CookieTypes { get; set; } = new List<MediaUsageCookieTypeRef>();
    public bool SiteSettings { get; set; }
}

public sealed class MediaUsagePageRef { public long Id { get; set; } public string Slug { get; set; } = ""; public string Title { get; set; } = ""; }
public sealed class MediaUsageSponsorRef { public long Id { get; set; } public string Name { get; set; } = ""; }
public sealed class MediaUsageCookieTypeRef { public long Id { get; set; } public string Name { get; set; } = ""; }

public sealed class ContentVersionInfoDto
{
    public long Id { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Label { get; set; }
    public string PublishedBy { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public int PageCount { get; set; }
    public int SectionCount { get; set; }
}

public sealed class ContentVersionDetailDto
{
    public long Id { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Label { get; set; }
    public string PublishedBy { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public int PageCount { get; set; }
    public int SectionCount { get; set; }
    public JsonElement Document { get; set; }
}

public sealed class ContentStatusDto
{
    public ContentVersionInfoDto? Published { get; set; }
    public string DraftSha256 { get; set; } = "";
    public bool HasUnpublishedChanges { get; set; }
    public IList<ProblemRefDto> Problems { get; set; } = new List<ProblemRefDto>();
    public DateTimeOffset? DraftUpdatedAt { get; set; }
}

public sealed class ContentBundleDto
{
    public JsonElement Content { get; set; }
    public IDictionary<string, MediaEntry> Media { get; set; } = new SortedDictionary<string, MediaEntry>(StringComparer.Ordinal);
    public IDictionary<string, string> Icons { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
}

public sealed class PreviewTokenDto
{
    public string Token { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ErrorResponse
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public JsonElement? Details { get; set; }
    public string RequestId { get; set; } = "";
}
