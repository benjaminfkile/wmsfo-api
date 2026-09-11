using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Auth;

// api.md 6.4: capabilities as endpoint metadata. Every /admin/* endpoint group
// carries .RequireCapability("<name>") with the names from contracts 3.6, and
// the three key endpoints carry .DenyApiKeys(). The Editor and Admin policies
// each gain the CapabilityOrGroupRequirement, which passes when the principal
// is a Cognito user in an admitted group, or an API key with api_key_all or a
// matching api_key_capability. When the endpoint carries DenyApiKeys metadata
// an API-key principal is refused with 403 forbidden.
public sealed class RequireCapabilityMetadata
{
    public string Capability { get; }
    public RequireCapabilityMetadata(string capability) => Capability = capability;
}

public sealed class DenyApiKeysMetadata { }

public static class EndpointCapabilityExtensions
{
    // Attach the endpoint's capability name (contracts 3.6). One call per
    // endpoint group. The CapabilityOrGroupRequirement handler reads this
    // to decide the API-key branch of the policy.
    public static TBuilder RequireCapability<TBuilder>(this TBuilder builder, string capability)
        where TBuilder : IEndpointConventionBuilder
    {
        var metadata = new RequireCapabilityMetadata(capability);
        return builder.WithMetadata(metadata);
    }

    // Mark the endpoint as Cognito-only: an API-key principal is refused with
    // 403 forbidden regardless of capability.
    public static TBuilder DenyApiKeys<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.WithMetadata(new DenyApiKeysMetadata());
    }
}

// The single requirement carried by both Editor and Admin policies. The list
// of admitted Cognito groups is passed in (Editor: editor+admin; Admin: admin).
// A Cognito user in any of those groups passes; an API key with api_key_all
// or a capability that matches the endpoint's RequireCapability metadata passes.
// Anything else fails, which surfaces as 403 forbidden through the standard
// authorization pipeline.
public sealed class CapabilityOrGroupRequirement : IAuthorizationRequirement
{
    public IReadOnlyList<string> AdmittedGroups { get; }
    public CapabilityOrGroupRequirement(params string[] admittedGroups) =>
        AdmittedGroups = admittedGroups;
}

public sealed class CapabilityOrGroupHandler : AuthorizationHandler<CapabilityOrGroupRequirement>
{
    private readonly IHttpContextAccessor _http;

    public CapabilityOrGroupHandler(IHttpContextAccessor http)
    {
        _http = http;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CapabilityOrGroupRequirement requirement)
    {
        var user = context.User;
        var isApiKey = ApiKeyAuthenticationHandler.IsApiKeyPrincipal(user);
        var endpoint = _http.HttpContext?.GetEndpoint();
        var denyApiKeys = endpoint?.Metadata.GetMetadata<DenyApiKeysMetadata>() is not null;
        var capability = endpoint?.Metadata.GetMetadata<RequireCapabilityMetadata>()?.Capability;

        if (isApiKey)
        {
            // The three API-key endpoints refuse a key principal, capabilities
            // notwithstanding (contracts 3.6, api.md 6.4).
            if (denyApiKeys)
            {
                return Task.CompletedTask;
            }
            // With no RequireCapability metadata there is no capability to
            // check against; keep the Cognito-only default (a legacy endpoint
            // without a capability marker cannot be reached by a key).
            if (capability is null)
            {
                return Task.CompletedTask;
            }
            var all = string.Equals(user.FindFirst(ApiKeyClaims.ApiKeyAll)?.Value, "true", StringComparison.Ordinal);
            if (all)
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }
            foreach (var claim in user.FindAll(ApiKeyClaims.ApiKeyCapability))
            {
                if (string.Equals(claim.Value, capability, StringComparison.Ordinal))
                {
                    context.Succeed(requirement);
                    return Task.CompletedTask;
                }
            }
            return Task.CompletedTask;
        }

        // Cognito principal: admit if any of the admitted groups is present.
        foreach (var group in requirement.AdmittedGroups)
        {
            if (HasGroup(user, group))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }
        }
        return Task.CompletedTask;
    }

    private static bool HasGroup(System.Security.Claims.ClaimsPrincipal user, string group)
    {
        foreach (var claim in user.FindAll(PersonClaims.Groups))
        {
            if (string.Equals(claim.Value, group, StringComparison.Ordinal)) return true;
            // Cognito can serialize groups as a comma-separated string.
            var parts = claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (string.Equals(part, group, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }
}
