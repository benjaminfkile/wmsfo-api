using System.Text.Json;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Tests;

// A131: a scheduled time at or before the clock has passed (contracts 4.5, 14).
public class ScheduleRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T18:00:00Z");

    [Fact]
    public void A_time_before_now_has_passed() =>
        Assert.True(ScheduleRules.HasPassed(Now.AddMilliseconds(-1), Now));

    [Fact]
    public void A_time_equal_to_now_has_passed() =>
        Assert.True(ScheduleRules.HasPassed(Now, Now));

    [Fact]
    public void A_time_after_now_has_not_passed() =>
        Assert.False(ScheduleRules.HasPassed(Now.AddMilliseconds(1), Now));

    [Fact]
    public void The_comparison_ignores_the_offset() =>
        Assert.False(ScheduleRules.HasPassed(DateTimeOffset.Parse("2026-10-22T12:00:01-06:00"), Now));

    [Fact]
    public void RequireAhead_lets_a_future_time_through() =>
        ScheduleRules.RequireAhead(Now.AddMinutes(1), Now);

    [Fact]
    public void RequireAhead_refuses_a_past_time_with_409_and_the_time_in_the_details()
    {
        var ex = Assert.Throws<ApiException>(() =>
            ScheduleRules.RequireAhead(DateTimeOffset.Parse("2026-10-22T09:30:00-06:00"), Now));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("scheduled_at_past", ex.Code);
        Assert.Equal("scheduled_at has passed", ex.Message);
        var details = Assert.IsType<JsonElement>(ex.Details);
        Assert.Equal("2026-10-22T15:30:00.000Z", details.GetProperty("scheduledAt").GetString());
    }
}
