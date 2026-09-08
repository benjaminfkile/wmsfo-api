using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wmsfo.Api.Contracts.Dtos;

// POST /realtime/authorize (contracts 2.4). Unknown fields ignored (contracts 0.2 exception).
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class RealtimeAuthorizeRequest
{
    public string Channel { get; set; } = "";
    public string? Credential { get; set; }
    public string ConnectionId { get; set; } = "";
}

public sealed class RealtimeAuthorizeResponse
{
    public bool Allow { get; set; }
    public string? Identity { get; set; }
}

// POST /realtime/message (contracts 2.5). Unknown fields ignored.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class RealtimeMessageRequest
{
    public string Channel { get; set; } = "";
    public string Event { get; set; } = "";
    public JsonElement Data { get; set; }
    public string ConnectionId { get; set; } = "";
    public string? Identity { get; set; }
}
