using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Wmsfo.Api.Http;

// api.md 5 step 4: the exception middleware. Maps every exception to the
// contracts 0.3 error body, sets Cache-Control: no-store, logs at Error for the
// unexpected ones (never returning the stack).
public static class ExceptionHandlingMiddleware
{
    public static void Use(IApplicationBuilder app, ILogger logger)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
                if (context.Response is { HasStarted: false, StatusCode: StatusCodes.Status413PayloadTooLarge })
                {
                    // A middleware upstream (Kestrel body-size guard, RateLimiter's rejection)
                    // may have written 413 with no body; fill it in with the error shape.
                    context.Response.ContentLength = null;
                    await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge,
                        ApiErrorCodes.PayloadTooLarge, "request body exceeds the limit", null);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client aborted; write nothing.
            }
            catch (ApiException ex)
            {
                await WriteErrorAsync(context, ex.StatusCode, ex.Code, ex.Message, ex.Details);
            }
            catch (BadHttpRequestException ex)
            {
                var (status, code, message) = MapBadHttpRequest(ex);
                await WriteErrorAsync(context, status, code, message, null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "wmsfo_internal_error");
                await WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                    ApiErrorCodes.InternalError, "internal error", null);
            }
        });
    }

    private static (int status, string code, string message) MapBadHttpRequest(BadHttpRequestException ex)
    {
        var status = ex.StatusCode;
        if (status == StatusCodes.Status413PayloadTooLarge)
        {
            return (status, ApiErrorCodes.PayloadTooLarge, "request body exceeds the limit");
        }
        if (status == StatusCodes.Status415UnsupportedMediaType)
        {
            return (status, ApiErrorCodes.UnsupportedMediaType, "content type not accepted here");
        }
        return (StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "malformed request");
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message, object? details)
    {
        if (context.Response.HasStarted) return;

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";

        // api.md 5: 429 carries Retry-After. rate_limited details carries retryAfterSeconds.
        if (status == StatusCodes.Status429TooManyRequests && details is RateLimitDetails rl)
        {
            context.Response.Headers.RetryAfter = rl.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var body = SerializeError(code, message, details, context.TraceIdentifier);
        await context.Response.Body.WriteAsync(body);
    }

    // Custom writer keeps the polymorphic `details` in one place without depending
    // on runtime-type serialization behaviour. Exposed so the readiness gate and
    // rate-limit rejection paths can produce the same body without going through
    // the exception handler.
    public static byte[] SerializeError(string code, string message, object? details, string requestId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);

            if (details is null)
            {
                writer.WriteNull("details");
            }
            else
            {
                writer.WritePropertyName("details");
                WriteDetails(writer, details);
            }

            writer.WriteString("requestId", requestId);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteDetails(Utf8JsonWriter writer, object details)
    {
        switch (details)
        {
            case ValidationDetails validation:
                writer.WriteStartObject();
                writer.WritePropertyName("fields");
                writer.WriteStartObject();
                foreach (var pair in validation.Fields)
                {
                    writer.WriteString(pair.Key, pair.Value);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case RateLimitDetails rate:
                writer.WriteStartObject();
                writer.WriteNumber("retryAfterSeconds", rate.RetryAfterSeconds);
                writer.WriteEndObject();
                break;
            default:
                // Fall back to STJ using the runtime type so callers can pass typed records.
                var runtimeType = details.GetType();
                JsonSerializer.Serialize(writer, details, runtimeType, Wmsfo.Api.Objects.CanonicalJson.Options);
                break;
        }
    }
}

public sealed record RateLimitDetails(int RetryAfterSeconds);
