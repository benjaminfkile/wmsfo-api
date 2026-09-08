using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace Wmsfo.Api.Http;

// api.md 5 step 2: ForwardedHeadersMiddleware with ForwardLimit =
// WMSFO_TRUSTED_PROXY_HOPS, known networks cleared, mounted with UseWhen on
// every path except /realtime/* (contracts 3.5: the callback guard needs the raw
// headers). After running, HttpContext.Connection.RemoteIpAddress is the client
// IP counted from the right of X-Forwarded-For.
public static class ForwardedHeadersPipeline
{
    public static IServiceCollection ConfigureForwardedHeaders(this IServiceCollection services, int trustedProxyHops)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = trustedProxyHops;
            options.RequireHeaderSymmetry = false;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
        return services;
    }

    public static IApplicationBuilder UseForwardedHeadersExceptRealtime(this IApplicationBuilder app)
    {
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/realtime"),
            branch => branch.UseForwardedHeaders());
        return app;
    }
}
