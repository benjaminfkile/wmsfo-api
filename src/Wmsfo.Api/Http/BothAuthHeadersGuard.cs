using Microsoft.AspNetCore.Http;

namespace Wmsfo.Api.Http;

// api.md 5 step 7: a request carrying both Authorization and X-Beacon-Key is
// 400 validation_failed before any scheme runs.
public static class BothAuthHeadersGuard
{
    public const string BeaconHeader = "X-Beacon-Key";
    public const string AuthorizationHeader = "Authorization";

    public static void Use(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var hasBeacon = context.Request.Headers.ContainsKey(BeaconHeader);
            var hasAuth = context.Request.Headers.ContainsKey(AuthorizationHeader);
            if (hasBeacon && hasAuth)
            {
                var v = new RequestValidation();
                v.Field("authorization", "cannot combine Authorization with X-Beacon-Key");
                v.Field("xBeaconKey", "cannot combine Authorization with X-Beacon-Key");
                v.ThrowIfInvalid();
            }
            await next();
        });
    }
}
