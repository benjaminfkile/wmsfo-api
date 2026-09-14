using System.Text.Json;
using System.Text.Json.Nodes;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: the delete's audit row carries `before = { ...dto, impact }`.
// This helper serializes the DTO and appends an `impact` property so the
// audit log reads like the DTO with one extra field, per contracts 4.5.
public static class ImpactBefore
{
    public static JsonElement Combine(object? dto, DeleteImpactDto impact)
    {
        JsonObject obj;
        if (dto is null)
        {
            obj = new JsonObject();
        }
        else
        {
            var json = JsonSerializer.Serialize(dto, CanonicalJson.Options);
            var parsed = JsonNode.Parse(json) as JsonObject
                ?? new JsonObject();
            obj = parsed;
        }
        var impactJson = JsonSerializer.Serialize(impact, CanonicalJson.Options);
        var impactNode = JsonNode.Parse(impactJson);
        obj["impact"] = impactNode;
        return JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString(CanonicalJson.Options));
    }
}
