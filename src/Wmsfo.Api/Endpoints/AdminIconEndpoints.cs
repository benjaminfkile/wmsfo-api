using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;

namespace Wmsfo.Api.Endpoints;

// api.md 11a.7: GET /admin/icons returns the built-in library ordered by name.
public static class AdminIconEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/icons",
            (IconLibrary library) =>
            {
                var items = library.Infos
                    .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(i => i.Id, StringComparer.Ordinal)
                    .Select(i => new IconInfoDto
                    {
                        Id = i.Id,
                        Name = i.Name,
                        Tags = i.Tags.ToList(),
                        Url = i.Url,
                    })
                    .ToList();
                return Results.Ok(new ItemsResponse<IconInfoDto> { Items = items });
            })
            .WithTags("AdminIcons")
            .Produces<ItemsResponse<IconInfoDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireCapability(ApiKeyCapabilities.Icons)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }
}
