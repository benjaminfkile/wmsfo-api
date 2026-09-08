using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Http;

// api.md 5 response conventions: `serverTime` (rfc3339) added by a result filter on
// beacon endpoints, stamped after the handler returned, which is after any
// transaction committed. Uses the canonical timestamp format (three fractional
// digits, `Z`) so beacons parse it with the same converter.
public static class ServerTimeFilter
{
    private const string Property = "serverTime";
    private static readonly Func<DateTimeOffset> DefaultClock = () => DateTimeOffset.UtcNow;

    public static RouteHandlerBuilder AddServerTime(this RouteHandlerBuilder builder)
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var result = await next(ctx);
            var now = ctx.HttpContext.RequestServices
                .GetService(typeof(IServerClock)) as IServerClock ?? new SystemServerClock();
            return WrapWithServerTime(result, now.UtcNow());
        });
        return builder;
    }

    // Exposed for the endpoints that build their own IResult and want to stamp
    // serverTime directly. The result filter is the primary consumer.
    internal static object? WrapWithServerTime(object? result, DateTimeOffset now)
    {
        if (result is null) return result;
        return new ServerTimeStampedResult(result, now);
    }

    private sealed class ServerTimeStampedResult : IResult
    {
        private readonly object _inner;
        private readonly DateTimeOffset _serverTime;

        public ServerTimeStampedResult(object inner, DateTimeOffset serverTime)
        {
            _inner = inner;
            _serverTime = serverTime;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var buffered = new BufferingResponseWrapper(httpContext);
            using (buffered)
            {
                var innerResult = _inner as IResult ?? Results.Ok(_inner);
                await innerResult.ExecuteAsync(httpContext);
                await buffered.FlushWithServerTimeAsync(_serverTime);
            }
        }
    }
}

// Injected clock so tests can freeze `serverTime` for byte comparisons.
public interface IServerClock
{
    DateTimeOffset UtcNow();
}

public sealed class SystemServerClock : IServerClock
{
    public DateTimeOffset UtcNow() => DateTimeOffset.UtcNow;
}

// Buffers the handler's body so we can splice in `serverTime` as the last key of
// its top-level JSON object. Everything else (status, headers) passes through.
internal sealed class BufferingResponseWrapper : IDisposable
{
    private readonly HttpContext _context;
    private readonly Stream _originalBody;
    private readonly MemoryStream _buffer = new();

    public BufferingResponseWrapper(HttpContext context)
    {
        _context = context;
        _originalBody = context.Response.Body;
        context.Response.Body = _buffer;
    }

    public async Task FlushWithServerTimeAsync(DateTimeOffset serverTime)
    {
        _context.Response.Body = _originalBody;
        if (_buffer.Length == 0)
        {
            return;
        }

        var bytes = _buffer.ToArray();
        var contentType = _context.Response.ContentType ?? "";
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await _originalBody.WriteAsync(bytes);
            return;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(bytes);
        }
        catch
        {
            await _originalBody.WriteAsync(bytes);
            return;
        }

        if (node is JsonObject obj)
        {
            var stamped = FormatServerTime(serverTime);
            if (obj.ContainsKey("serverTime")) obj.Remove("serverTime");
            obj["serverTime"] = stamped;
            var serialized = JsonSerializer.SerializeToUtf8Bytes(obj, CanonicalJson.Options);
            _context.Response.ContentLength = serialized.Length;
            await _originalBody.WriteAsync(serialized);
        }
        else
        {
            await _originalBody.WriteAsync(bytes);
        }
    }

    private static string FormatServerTime(DateTimeOffset when) =>
        when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _context.Response.Body = _originalBody;
        _buffer.Dispose();
    }
}
