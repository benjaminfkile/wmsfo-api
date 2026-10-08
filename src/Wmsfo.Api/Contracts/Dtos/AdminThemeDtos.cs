using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wmsfo.Api.Contracts.Dtos;

// One tracker theme (contracts 4.0 TrackerTheme): GET /admin/themes and every
// theme write's answer.
public sealed class TrackerThemeDto
{
    public long Id { get; set; }
    public string Renderer { get; set; } = "";
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    // The style body on the CDN (themes/{sha256}.json) and its canonical size.
    public string StyleUrl { get; set; } = "";
    public string StyleSha256 { get; set; } = "";
    public int StyleBytes { get; set; }
    // The confirmed sprite set's index hash and its sprite base; both null
    // until a sprite confirm passed.
    public string? SpriteSha256 { get; set; }
    public string? SpriteUrl { get; set; }
    public Chrome Chrome { get; set; } = new();
    public Overlay Overlay { get; set; } = new();
    public string? ThumbnailMediaId { get; set; }
    public bool DefaultLightMode { get; set; }
    public bool DefaultDarkMode { get; set; }
    // Events that enable it.
    public int EventCount { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public AuditStampDto? Audit { get; set; }
}

// POST /admin/themes as JSON; the multipart form carries the same fields with
// `style` as a file part. Style, Chrome, Overlay, and ThumbnailMediaId are
// bare JsonElements so the endpoint reports each rule at its path.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateTrackerThemeRequest
{
    public string Renderer { get; set; } = "";
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int? SortOrder { get; set; }
    public JsonElement Style { get; set; }
    public JsonElement Chrome { get; set; }
    public JsonElement Overlay { get; set; }
    public JsonElement ThumbnailMediaId { get; set; }
}

// PATCH /admin/themes/{id}: any subset; `renderer` is immutable (400).
// ThumbnailMediaId: an id sets it, null clears it, absent leaves it.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PatchTrackerThemeRequest
{
    public string? Key { get; set; }
    public string? Name { get; set; }
    public int? SortOrder { get; set; }
    public JsonElement Style { get; set; }
    public JsonElement Chrome { get; set; }
    public JsonElement Overlay { get; set; }
    public JsonElement ThumbnailMediaId { get; set; }
}

// POST /admin/themes/{id}/sprite: the lowercase hex SHA-256 of the canonical
// bytes of the sprite.json the panel is about to upload.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SpriteTicketsRequest
{
    public string IndexSha256 { get; set; } = "";
}

// The four presigned PUTs of a sprite set (contracts 4.0 SpriteTickets).
public sealed class SpriteTicketsDto
{
    public string IndexSha256 { get; set; } = "";
    public IList<SpriteUploadDto> Uploads { get; set; } = new List<SpriteUploadDto>();
}

public sealed class SpriteUploadDto
{
    // sprite.json, sprite.png, sprite@2x.json, or sprite@2x.png.
    public string File { get; set; } = "";
    public string UploadUrl { get; set; } = "";
    public string Method { get; set; } = "PUT";
    public IDictionary<string, string> Headers { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    public DateTimeOffset ExpiresAt { get; set; }
}

// POST /admin/themes/{id}/sprite/confirm: the prefix to confirm.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SpriteConfirmRequest
{
    public string IndexSha256 { get; set; } = "";
}

// POST /admin/themes/{id}/default: each flag optional; an absent flag is left
// as it is.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SetThemeDefaultRequest
{
    public bool? Light { get; set; }
    public bool? Dark { get; set; }
}

// DELETE /admin/themes/{id}: an optional body naming another theme of the
// same renderer that takes the deleted theme's events and default flags.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DeleteTrackerThemeRequest
{
    public long? ReplacementId { get; set; }
}
