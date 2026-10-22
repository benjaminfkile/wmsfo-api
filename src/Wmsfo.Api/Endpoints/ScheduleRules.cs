using System.Text.Json;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints;

// Contracts 4.5 and 14: an event in status 2 carries a scheduled time that is
// still ahead. A time at or before the clock has passed; the refusal is
// `409 scheduled_at_past` with `details.scheduledAt` the refused time as RFC 3339.
public static class ScheduleRules
{
    public const string ScheduledAtPast = "scheduled_at_past";

    public static bool HasPassed(DateTimeOffset scheduledAt, DateTimeOffset now) => scheduledAt <= now;

    public static ApiException Passed(DateTimeOffset scheduledAt)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(new { scheduledAt }, CanonicalJson.Options);
        using var doc = JsonDocument.Parse(serialized);
        return new ApiException(StatusCodes.Status409Conflict, ScheduledAtPast,
            "scheduled_at has passed", doc.RootElement.Clone());
    }

    // Throws Passed when the time is at or before now.
    public static void RequireAhead(DateTimeOffset scheduledAt, DateTimeOffset now)
    {
        if (HasPassed(scheduledAt, now)) throw Passed(scheduledAt);
    }
}
