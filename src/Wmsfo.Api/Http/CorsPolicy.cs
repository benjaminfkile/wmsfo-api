using Microsoft.Extensions.DependencyInjection;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Http;

// api.md 5 step 6: one CORS policy with the exact origins from
// WMSFO_CORS_ORIGINS, methods GET, POST, PUT, PATCH, DELETE, headers
// Authorization, Content-Type, X-Beacon-Key, X-App-Version, preflight cache 600 s,
// credentials off. Applied to every route except the two callbacks and /api/health.
public static class WmsfoCors
{
    public const string PolicyName = "wmsfo";

    public static IServiceCollection AddWmsfoCors(this IServiceCollection services, WmsfoOptions options)
    {
        var origins = options.CorsOriginList();
        services.AddCors(cors => cors.AddPolicy(PolicyName, builder =>
        {
            builder.WithOrigins(origins.ToArray())
                .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                .WithHeaders("Authorization", "Content-Type", "X-Beacon-Key", "X-App-Version")
                .SetPreflightMaxAge(TimeSpan.FromSeconds(600));
            // credentials stay off (the default)
        }));
        return services;
    }
}
