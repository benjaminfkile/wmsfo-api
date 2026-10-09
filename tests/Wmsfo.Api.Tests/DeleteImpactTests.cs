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
        Assert.Equal(
            "This is the only Google theme enabled on Liftoff 2026. Enable another there first.",
            string.Format(CultureInfo.InvariantCulture, ImpactHelpers.LastGoogleThemeBlocked, "Liftoff 2026"));
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
        Assert.Equal(
            "The maplibre renderer loses its default dark theme.",
            string.Format(CultureInfo.InvariantCulture, ImpactHelpers.ThemeDefaultWarning, "maplibre", "dark"));
        Assert.Equal(
            "2 events lose their map; their viewers get Google Maps at the next snapshot.",
            string.Format(CultureInfo.InvariantCulture, ImpactHelpers.MapEventsWarning, 2));
        Assert.Equal(
            "Liftoff 2026 is the current event; its viewers move to Google Maps at the next snapshot.",
            string.Format(CultureInfo.InvariantCulture, ImpactHelpers.MapCurrentEventWarning, "Liftoff 2026"));
        Assert.Equal("Liftoff 2026 is live.",
            string.Format(CultureInfo.InvariantCulture, ImpactHelpers.MapLiveEventWarning, "Liftoff 2026"));
    }

    [Fact]
    public void Names_cap_is_ten()
    {
        Assert.Equal(10, ImpactHelpers.NamesCap);
    }
}
