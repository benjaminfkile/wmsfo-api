using System.Text.Json;

namespace Wmsfo.Api.Data;

// One class per sql.md 3 table. Names are singular so EFCore.NamingConventions
// yields the snake_case table names. Column mapping details live in
// WmsfoDbContext.OnModelCreating.

public sealed class EventStatus
{
    public short Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Route
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string S3Key { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int PointCount { get; set; }
    public string UploadedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Event
{
    public long Id { get; set; }
    public int Year { get; set; }
    public string Name { get; set; } = "";
    public short StatusId { get; set; }
    public bool IsCurrent { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? WentLiveAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int FundsPercent { get; set; }
    public long? RouteId { get; set; }
    public JsonDocument? FinalCookieTally { get; set; }
    public long NextSeq { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EventStatusHistory
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public short? FromStatusId { get; set; }
    public short ToStatusId { get; set; }
    public string ChangedBy { get; set; } = "";
    public DateTimeOffset ChangedAt { get; set; }
}

public sealed class EventMessage
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public string Body { get; set; } = "";
    public DateTimeOffset? EventTime { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Beacon
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Role { get; set; } = "";
    public byte[] KeyHash { get; set; } = Array.Empty<byte>();
    public string KeyPrefix { get; set; } = "";
    public int KeyVersion { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastLocationAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? StaleSince { get; set; }
    public JsonDocument? Telemetry { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BeaconEnrollmentToken
{
    public long Id { get; set; }
    public long BeaconId { get; set; }
    public byte[] TokenHash { get; set; } = Array.Empty<byte>();
    public byte[]? KeyCiphertext { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BeaconLog
{
    public long Id { get; set; }
    public long BeaconId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? AppVersion { get; set; }
    public int SizeBytes { get; set; }
    public string Body { get; set; } = "";
}

public sealed class Location
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public long BeaconId { get; set; }
    public long Seq { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double? SpeedMps { get; set; }
    public double? AltitudeM { get; set; }
    public double? HeadingDeg { get; set; }
    public double? AccuracyM { get; set; }
    public bool Published { get; set; }
}

public sealed class Sponsor
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
    public Guid? LogoMediaId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SponsorYear
{
    public long Id { get; set; }
    public long SponsorId { get; set; }
    public int EventYear { get; set; }
    public decimal? AmountDonated { get; set; }
    public bool Active { get; set; }
    public bool CanAdvertise { get; set; }
    public bool Anonymous { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
}

public sealed class Person
{
    public long Id { get; set; }
    public Guid CognitoSub { get; set; }
    public string Email { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class Subscriber
{
    public long Id { get; set; }
    public long PersonId { get; set; }
    public string Channel { get; set; } = "";
    public string Address { get; set; } = "";
    public byte[]? VerifyTokenHash { get; set; }
    public DateTimeOffset? VerifyExpiresAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string UnsubscribeToken { get; set; } = "";
    public DateTimeOffset? UnsubscribedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CookieType
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public JsonDocument? Icon { get; set; }
    public int Sort { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Cookie
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public long PersonId { get; set; }
    public long CookieTypeId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset LeftAt { get; set; }
    public DateTimeOffset? HiddenAt { get; set; }
    public string? HiddenBy { get; set; }
}

public sealed class ContactMessage
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Body { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AppSetting
{
    public string Key { get; set; } = "";
    public JsonDocument Value { get; set; } = default!;
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Snapshot
{
    public short Id { get; set; }
    public long Version { get; set; }
    public string Url { get; set; } = "";
    public string S3Key { get; set; } = "";
    public DateTimeOffset BuiltAt { get; set; }
}

public sealed class LiveState
{
    public short Id { get; set; }
    public DateTimeOffset? LastWriteAt { get; set; }
    public long? LastWriteSeq { get; set; }
    public long? LastWriteVersion { get; set; }
    public string? LastWriteError { get; set; }
    public string? LastWriteNode { get; set; }
}

public sealed class Outbox
{
    public long Id { get; set; }
    public string Topic { get; set; } = "";
    public JsonDocument Payload { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class AlertDelivery
{
    public long Id { get; set; }
    public long OutboxId { get; set; }
    public long SubscriberId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? SesMessageId { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class MediaAsset
{
    public Guid Id { get; set; }
    public string Filename { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "";
    public string S3Key { get; set; } = "";
    public long? SizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Sha256 { get; set; }
    public JsonDocument Variants { get; set; } = default!;
    public string Alt { get; set; } = "";
    public string Title { get; set; } = "";
    public string UploadedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? UnreferencedSince { get; set; }
    public DateTimeOffset? OrphanedAt { get; set; }
}

public sealed class Page
{
    public long Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? NavLabel { get; set; }
    public int NavPosition { get; set; }
    public bool IsHidden { get; set; }
    public string Role { get; set; } = "none";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Section
{
    public long Id { get; set; }
    public long PageId { get; set; }
    public string Kind { get; set; } = "";
    public int Position { get; set; }
    public bool IsHidden { get; set; }
    public JsonDocument Data { get; set; } = default!;
    public JsonDocument Presentation { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SectionItem
{
    public long Id { get; set; }
    public long SectionId { get; set; }
    public int Position { get; set; }
    public bool IsHidden { get; set; }
    public JsonDocument Data { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SiteSettingDraft
{
    public short Id { get; set; }
    public JsonDocument Data { get; set; } = default!;
    public string? UpdatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class ContentVersion
{
    public long Id { get; set; }
    public JsonDocument Document { get; set; } = default!;
    public string Sha256 { get; set; } = "";
    public Guid[] MediaIds { get; set; } = Array.Empty<Guid>();
    public string? Label { get; set; }
    public string PublishedBy { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
}

public sealed class PreviewToken
{
    public long Id { get; set; }
    public byte[] TokenHash { get; set; } = Array.Empty<byte>();
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class IconLibraryState
{
    public short Id { get; set; }
    public string? LibrarySha256 { get; set; }
    public DateTimeOffset? WrittenAt { get; set; }
}
