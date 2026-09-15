using System.Text.Json;

namespace Wmsfo.Api.Contracts.Dtos;

public sealed class BeaconsListResponse
{
    public IList<BeaconDto> Items { get; set; } = new List<BeaconDto>();
    public int StaleAfterS { get; set; }
}

public sealed class CreateBeaconRequest
{
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class PatchBeaconRequest
{
    public string? Name { get; set; }
    public string? Notes { get; set; }
    // A37: null clears the override (fall back to the setting), a number sets
    // it, absent leaves the row unchanged. Deserialized as a bare JsonElement
    // so JSON `null` yields `ValueKind == Null` (STJ collapses `JsonElement?`
    // JSON null to a C# null and we would not be able to tell it from absent).
    public JsonElement MinIntervalMs { get; set; }
    public bool? HubAllowed { get; set; }
}

public sealed class BeaconWithKeyResponse
{
    public BeaconDto Beacon { get; set; } = new();
    public string Key { get; set; } = "";
    public EnrollmentDto Enrollment { get; set; } = new();
}
