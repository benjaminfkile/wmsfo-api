using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Wmsfo.Api.Http;

// sql.md 4.3: the API catches PostgresException with SqlState = '23505' and
// switches on ConstraintName. This helper is the single mapping used by every
// admin write.
public static class ConstraintErrorMapping
{
    // The exact index / constraint names sql.md 3 gives.
    public const string EventYearKey = "event_year_key";
    public const string PageSlugKey = "page_slug_key";
    public const string EventOneLive = "event_one_live";
    public const string EventOneCurrent = "event_one_current";
    public const string BeaconOneActive = "beacon_one_active";
    public const string RouteS3KeyKey = "route_s3_key_key";
    public const string SubscriberChannelAddressKey = "subscriber_channel_address_key";

    // Returns null when the exception is not one this table maps; the caller
    // (typically a catch block) then rethrows so a 500 is produced.
    public static ApiException? Map(PostgresException ex)
    {
        if (ex.SqlState != "23505") return null;
        return ex.ConstraintName switch
        {
            EventYearKey => new ApiException(StatusCodes.Status409Conflict, "year_taken", "year already used"),
            PageSlugKey => new ApiException(StatusCodes.Status409Conflict, "slug_taken", "slug already used"),
            EventOneLive => new ApiException(StatusCodes.Status409Conflict, "another_event_live", "another event is live"),
            SubscriberChannelAddressKey => new ApiException(StatusCodes.Status409Conflict, "address_taken", "address already subscribed"),
            // event_one_current, beacon_one_active: retry-once territory; leave
            // for the caller to handle. Others are not mapped and become 500.
            _ => null,
        };
    }
}
