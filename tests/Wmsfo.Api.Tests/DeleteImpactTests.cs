using System.Globalization;
using Wmsfo.Api.Endpoints.Impact;

namespace Wmsfo.Api.Tests;

// Unit tests for the fixed sentences that ImpactQueries produce (api.md 5b)
// and for the ten-name cap. The queries themselves are exercised by the
// integration tests; here we just verify the strings and the cap.
public class DeleteImpactTests
{
    [Fact]
    public void Blocked_sentences_read_as_documented()
    {
        Assert.Equal("This event is live. End it first.", ImpactHelpers.LiveEventBlocked);
        Assert.Equal("This is the current event. Make another event current first.", ImpactHelpers.CurrentEventBlocked);
    }

    [Fact]
    public void Warning_sentences_read_as_documented()
    {
        Assert.Equal("This beacon is active; the live feed stops.", ImpactHelpers.BeaconActiveWarning);
        Assert.Equal(
            "This page holds the live role; pick the page that takes it.",
            string.Format(CultureInfo.InvariantCulture, ImpactHelpers.PageRoleWarning, "live"));
        Assert.Equal(
            "An event is live; its cookie tally drops by 42.",
            string.Format(CultureInfo.InvariantCulture, ImpactHelpers.CookieTallyWarning, 42));
    }

    [Fact]
    public void Names_cap_is_ten()
    {
        Assert.Equal(10, ImpactHelpers.NamesCap);
    }
}
