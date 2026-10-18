using System.Text.Json;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Endpoints;

// Contracts 4.2 `PUT /beacons/me/state` body rules. The body is one JSON object
// with exactly two keys, both required: `state`, a JSON object (not an array,
// not null) nested at most 8 levels, counted as for the heartbeat's `debug`;
// and `expectedVersion`, an int64 of 0 or more. Any other key, a repeated key,
// or a body that is not JSON is `400 validation_failed`. The 16 KB cap is
// enforced when the body is read, before this runs.
public static class BeaconStateRules
{
    public const int StateMaxDepth = 8;

    public sealed record Parsed(string StateJson, long ExpectedVersion);

    public static Parsed Parse(ReadOnlySpan<byte> body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed, "malformed beacon state body");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                RequestValidation.Throw("body", "must be an object");
            }

            var v = new RequestValidation();
            JsonElement? state = null;
            JsonElement? expected = null;
            foreach (var prop in root.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "state":
                        if (state is not null) v.Field("state", "appears more than once");
                        state = prop.Value;
                        break;
                    case "expectedVersion":
                        if (expected is not null) v.Field("expectedVersion", "appears more than once");
                        expected = prop.Value;
                        break;
                    default:
                        v.Field(prop.Name, "is not allowed");
                        break;
                }
            }

            string stateJson = "";
            if (state is not JsonElement s)
            {
                v.Field("state", "required");
            }
            else if (s.ValueKind != JsonValueKind.Object)
            {
                v.Field("state", "must be an object");
            }
            else if (BeaconEndpoints.JsonDepth(s) > StateMaxDepth)
            {
                v.Field("state", $"nesting exceeds the {StateMaxDepth}-level limit");
            }
            else
            {
                stateJson = s.GetRawText();
            }

            long expectedVersion = 0;
            if (expected is not JsonElement e)
            {
                v.Field("expectedVersion", "required");
            }
            else if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out expectedVersion))
            {
                v.Field("expectedVersion", "must be an integer");
            }
            else if (expectedVersion < 0)
            {
                v.Field("expectedVersion", "must be 0 or more");
            }

            v.ThrowIfInvalid();
            return new Parsed(stateJson, expectedVersion);
        }
    }
}
