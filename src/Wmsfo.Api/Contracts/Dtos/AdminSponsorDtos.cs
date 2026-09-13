using System.Text.Json.Serialization;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Contracts.Dtos;

public sealed class CreateSponsorRequest
{
    public string Name { get; set; } = "";
    public string? ContactPerson { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? FbUrl { get; set; }
    public string? IgUrl { get; set; }
}

public sealed class PatchSponsorRequest
{
    public string? Name { get; set; }
    public string? ContactPerson { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? FbUrl { get; set; }
    public string? IgUrl { get; set; }
    public string? LogoMediaId { get; set; }
}

public sealed class UpsertSponsorYearRequest
{
    [JsonConverter(typeof(NullableAmountDonatedDecimalConverter))]
    public decimal? AmountDonated { get; set; }
    public bool Active { get; set; }
    public bool CanAdvertise { get; set; }
    public bool Anonymous { get; set; }
    public int? PinnedPosition { get; set; }
    public int? LingerMsOverride { get; set; }
}

// POST /admin/sponsors/import (contracts 4.5 Sponsors, sql.md 8.4c).
public sealed class SponsorImportRequest
{
    public int FromYear { get; set; }
    public int ToYear { get; set; }
    public IList<long> SponsorIds { get; set; } = new List<long>();
}

public sealed class SponsorImportResponse
{
    public int Created { get; set; }
    public int Skipped { get; set; }
}
