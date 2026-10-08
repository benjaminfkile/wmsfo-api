using System.Text.Json.Serialization;

namespace Wmsfo.Api.Contracts.Dtos;

// One link under a help topic (contracts 4.0 HelpLink): `to` is a path of the
// admin panel (`/events`) or an https URL.
public sealed class HelpLinkDto
{
    public string Label { get; set; } = "";
    public string To { get; set; } = "";
}

// One help topic (contracts 4.0 HelpTopic): what GET /admin/help lists and
// every write answers. `edited` is true while an admin's text replaces the
// seed's; `defaultChanged` is true when the seed's text changed after that
// edit.
public sealed class HelpTopicDto
{
    public string Key { get; set; } = "";
    public string Page { get; set; } = "";
    public string Label { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<HelpLinkDto> Links { get; set; } = new();
    public bool Edited { get; set; }
    public string? EditedBy { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public bool DefaultChanged { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public AuditStampDto? Audit { get; set; }
}

// PUT /admin/help/{key}. Any other field is 400.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PutHelpTopicRequest
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public List<PutHelpLinkRequest?>? Links { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PutHelpLinkRequest
{
    public string? Label { get; set; }
    public string? To { get; set; }
}
