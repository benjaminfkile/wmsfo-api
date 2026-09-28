using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wmsfo.Api.Contracts.Dtos;

// One row of GET /admin/posters (contracts 4.5 Posters).
public sealed class PosterSummaryDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long? RouteId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// The full poster: GET /admin/posters/{id} and every write's answer. Layout is
// the panel's opaque document in canonical form (properties in ordinal
// order); null when unset.
public sealed class PosterDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long? RouteId { get; set; }
    public JsonElement? Layout { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public AuditStampDto? Audit { get; set; }
}

// POST /admin/posters. RouteId and Layout are bare JsonElements so the
// endpoint applies the same rules as PATCH: a route id or a JSON object sets
// the value, null or absent leaves it null. Any other field is 400.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreatePosterRequest
{
    public string Name { get; set; } = "";
    public JsonElement RouteId { get; set; }
    public JsonElement Layout { get; set; }
}

// PATCH /admin/posters/{id}. RouteId and Layout are bare JsonElements so JSON
// `null` (clear) yields `ValueKind == Null` and absent (leave unchanged)
// yields `Undefined`. Any other field is 400.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PatchPosterRequest
{
    public string? Name { get; set; }
    public JsonElement RouteId { get; set; }
    public JsonElement Layout { get; set; }
}
