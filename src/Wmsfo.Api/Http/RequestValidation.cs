using System.Collections.Generic;

namespace Wmsfo.Api.Http;

// api.md 5: field-level validation collector. Handlers call Field(name, message)
// for every rule that fails, then Throw() at the end. The exception carries
// details.fields (a map of field path to human-readable message) per contracts 0.3.
public sealed class RequestValidation
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);

    public bool HasErrors => _fields.Count > 0;

    public RequestValidation Field(string field, string message)
    {
        // Keep the first message per field; validators list rules top down.
        _fields.TryAdd(field, message);
        return this;
    }

    public IReadOnlyDictionary<string, string> Fields => _fields;

    public void ThrowIfInvalid()
    {
        if (!HasErrors) return;
        var first = _fields.First();
        throw new ApiException(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.ValidationFailed,
            $"{first.Key} {first.Value}",
            new ValidationDetails(new Dictionary<string, string>(_fields, StringComparer.Ordinal)));
    }

    // Convenience for the both-headers rule and other one-shot cases.
    public static void Throw(string field, string message)
    {
        var v = new RequestValidation();
        v.Field(field, message);
        v.ThrowIfInvalid();
    }
}

public sealed record ValidationDetails(IReadOnlyDictionary<string, string> Fields);
