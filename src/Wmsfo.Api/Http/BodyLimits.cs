using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Wmsfo.Api.Http;

// api.md 5 body size limits: JSON default 64 KB, section / item / site settings
// 256 KB, route uploads 5 MB, beacon logs 2 MB, heartbeats 8 KB. Attached as
// endpoint metadata; the middleware below reads the metadata on the matched
// endpoint and reconfigures the request's max body size. Kestrel's default
// (~28 MB) is a safety cap only.
public sealed record RequestBodyLimit(long MaxBytes);

public static class BodyLimits
{
    public const long JsonDefault = 64L * 1024;
    public const long SectionOrItem = 256L * 1024;
    public const long Heartbeat = 8L * 1024;
    public const long BeaconLog = 2L * 1024 * 1024;
    public const long RouteUpload = 5L * 1024 * 1024;

    public static void Use(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            var limit = endpoint?.Metadata.GetMetadata<RequestBodyLimit>();
            if (limit is not null)
            {
                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false })
                {
                    feature.MaxRequestBodySize = limit.MaxBytes;
                }
                if (context.Request.ContentLength is long declared && declared > limit.MaxBytes)
                {
                    throw new ApiException(StatusCodes.Status413PayloadTooLarge,
                        ApiErrorCodes.PayloadTooLarge,
                        $"request body of {declared} bytes exceeds the {limit.MaxBytes}-byte limit");
                }
            }
            await next();
        });
    }

    public static RouteHandlerBuilder WithBodyLimit(this RouteHandlerBuilder builder, long maxBytes)
    {
        builder.WithMetadata(new RequestBodyLimit(maxBytes));
        return builder;
    }
}
