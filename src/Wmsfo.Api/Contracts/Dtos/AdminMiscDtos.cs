using System.Text.Json;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Contracts.Dtos;

public sealed class CreateCookieTypeRequest
{
    public string Name { get; set; } = "";
    public int Sort { get; set; }
    public bool Active { get; set; }
    public IconValue? Icon { get; set; }
}

public sealed class PatchCookieTypeRequest
{
    public string? Name { get; set; }
    public int? Sort { get; set; }
    public bool? Active { get; set; }
    public IconValue? Icon { get; set; }
}

public sealed class CreatePageRequest
{
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? NavLabel { get; set; }
    public int NavPosition { get; set; }
    public bool IsHidden { get; set; }
}

public sealed class PatchPageRequest
{
    public string? Slug { get; set; }
    public string? Title { get; set; }
    public string? NavLabel { get; set; }
    public int? NavPosition { get; set; }
    public bool? IsHidden { get; set; }
}

public sealed class PageOrderRequest
{
    public IList<long> Ids { get; set; } = new List<long>();
}

public sealed class CreateSectionRequest
{
    public string Kind { get; set; } = "";
    public int? Position { get; set; }
    public JsonElement? Data { get; set; }
    public PresentationDto? Presentation { get; set; }
}

public sealed class PatchSectionRequest
{
    public JsonElement? Data { get; set; }
    public PresentationDto? Presentation { get; set; }
    public bool? IsHidden { get; set; }
}

public sealed class MoveSectionRequest
{
    public long PageId { get; set; }
    public int Position { get; set; }
}

public sealed class SectionOrderRequest
{
    public IList<long> Ids { get; set; } = new List<long>();
}

public sealed class CreateSectionItemRequest
{
    public JsonElement? Data { get; set; }
    public int? Position { get; set; }
}

public sealed class PatchSectionItemRequest
{
    public JsonElement? Data { get; set; }
    public bool? IsHidden { get; set; }
}

public sealed class ItemOrderRequest
{
    public IList<long> Ids { get; set; } = new List<long>();
}

public sealed class SiteSettingsUpdateRequest
{
    public JsonElement Data { get; set; }
}

public sealed class PublishContentRequest
{
    public string? Label { get; set; }
}

public sealed class MediaUploadUrlRequest
{
    public string Filename { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Alt { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class MediaPatchRequest
{
    public string? Alt { get; set; }
    public string? Title { get; set; }
}

public sealed class SettingUpdateRequest
{
    public JsonElement Value { get; set; }
}

public sealed class SubscribersSummaryResponse
{
    public int Verified { get; set; }
    public int Pending { get; set; }
    public int Unsubscribed { get; set; }
}

// GET /admin/live response (contracts 4.5).
public sealed class AdminLiveResponse
{
    public DateTimeOffset? LastWriteAt { get; set; }
    public long? LastWriteSeq { get; set; }
    public long? LastWriteVersion { get; set; }
    public string? LastWriteError { get; set; }
    public string? LastWriteNode { get; set; }
    public AdminLiveNode Node { get; set; } = new();
}

public sealed class AdminLiveNode
{
    public string? Instance { get; set; }
    public bool IsLeader { get; set; }
    public DateTimeOffset? LeaderEvaluatedAt { get; set; }
    public DateTimeOffset CacheRefreshedAt { get; set; }
    public LiveObject Live { get; set; } = new();
    // api.md 16: the in-memory counter snapshot for this node.
    public NodeCountersSnapshot Counters { get; set; } = new();
}
