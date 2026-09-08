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
}
