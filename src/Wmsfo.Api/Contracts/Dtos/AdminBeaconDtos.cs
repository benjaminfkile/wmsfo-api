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
    public string Role { get; set; } = "";
}

public sealed class PatchBeaconRequest
{
    public string? Name { get; set; }
    public string? Notes { get; set; }
}

public sealed class BeaconWithKeyResponse
{
    public BeaconDto Beacon { get; set; } = new();
    public string Key { get; set; } = "";
    public EnrollmentDto Enrollment { get; set; } = new();
}
