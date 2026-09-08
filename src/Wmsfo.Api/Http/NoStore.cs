using Microsoft.AspNetCore.Http;

namespace Wmsfo.Api.Http;

// api.md 5 response conventions: Cache-Control: no-store on every API response.
// Set on the response before the handler runs so callers can still override it
// per-route (e.g. CDN objects the API serves in some diagnostic paths).
public static class NoStoreMiddleware
{
    public static void Use(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(state =>
            {
                var ctx = (HttpContext)state;
                if (!ctx.Response.Headers.ContainsKey("Cache-Control"))
                {
                    ctx.Response.Headers.CacheControl = "no-store";
                }
                return Task.CompletedTask;
            }, context);
            await next();
        });
    }
}
