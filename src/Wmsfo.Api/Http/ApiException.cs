using System.Text.Json.Serialization;

namespace Wmsfo.Api.Http;

// api.md 5 exception model. Every handler that wants a documented error code
// throws one of these; the exception middleware maps it to the contracts 0.3
// error body and the HTTP status.
public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }
    public object? Details { get; }

    public ApiException(int statusCode, string code, string message, object? details = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Details = details;
    }
}

// Contracts 0.3 error body: code, message, details, requestId.
public sealed class ErrorResponse
{
    [JsonPropertyName("code")] public string Code { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("details")] public object? Details { get; init; }
    [JsonPropertyName("requestId")] public string RequestId { get; init; } = "";
}

// The codes the pipeline itself produces (contracts 4.7). Handlers add their own.
public static class ApiErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string MfaRequired = "mfa_required";
    public const string NotFound = "not_found";
    public const string PayloadTooLarge = "payload_too_large";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string RateLimited = "rate_limited";
    public const string InternalError = "internal_error";
    public const string UpstreamFailed = "upstream_failed";
    public const string Unavailable = "unavailable";

    // A13: content working set + content pipeline.
    public const string SlugReserved = "slug_reserved";
    public const string SlugTaken = "slug_taken";
    public const string PageHasRole = "page_has_role";
    public const string UnknownKind = "unknown_kind";
    public const string KindNotAllowed = "kind_not_allowed";
    public const string ContentUnchanged = "content_unchanged";
    public const string ContentInvalid = "content_invalid";
    public const string PreviewTokenInvalid = "preview_token_invalid";

    // A26: /admin/api-keys mint (contracts 4.5 API keys).
    public const string NameTaken = "name_taken";
}
