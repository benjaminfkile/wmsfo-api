using System.Text.Json;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Tests;

// A137: `tracker.theme` in the PUT /me/preferences body (contracts 4.4) is a
// tracker_theme.key slug, ^[a-z][a-z0-9-]{1,39}$, checked for shape only.
public class MePreferencesRulesTests
{
    [Theory]
    [InlineData("expedition")]
    [InlineData("ab")]
    [InlineData("a1")]
    [InlineData("dark-2")]
    [InlineData("a-")]
    public void A_slug_is_accepted_and_kept(string key)
    {
        var dto = Parse($"{{\"theme\":null,\"tracker\":{{\"theme\":{JsonSerializer.Serialize(key)}}}}}");
        Assert.Equal(key, dto.Tracker.Theme);
    }

    [Fact]
    public void A_forty_character_slug_is_accepted()
    {
        var key = "a" + new string('b', 39);
        Assert.Equal(key, Parse($"{{\"theme\":null,\"tracker\":{{\"theme\":\"{key}\"}}}}").Tracker.Theme);
    }

    [Fact]
    public void An_absent_theme_stays_null()
    {
        Assert.Null(Parse("{\"theme\":\"dark\",\"tracker\":{\"snow\":true}}").Tracker.Theme);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"\"")]
    [InlineData("\"a\"")]
    [InlineData("\"Expedition\"")]
    [InlineData("\"1dark\"")]
    [InlineData("\"-x\"")]
    [InlineData("\"dark_mode\"")]
    [InlineData("\"dark\\n\"")]
    [InlineData("\"a" + "bbbbbbbbbb" + "bbbbbbbbbb" + "bbbbbbbbbb" + "bbbbbbbbbb" + "\"")]
    public void Any_other_value_names_tracker_theme(string value)
    {
        var ex = Assert.Throws<ApiException>(() => Parse($"{{\"theme\":null,\"tracker\":{{\"theme\":{value}}}}}"));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        var details = Assert.IsType<ValidationDetails>(ex.Details);
        Assert.Equal(new[] { "tracker.theme" }, details.Fields.Keys.ToArray());
    }

    [Fact]
    public void The_slug_rule_matches_the_admin_theme_key_rule()
    {
        Assert.True(MePreferencesRules.IsTrackerThemeKey("light"));
        Assert.False(MePreferencesRules.IsTrackerThemeKey(null));
        Assert.False(MePreferencesRules.IsTrackerThemeKey("l"));
        Assert.False(MePreferencesRules.IsTrackerThemeKey(new string('a', 41)));
    }

    private static Wmsfo.Api.Contracts.Dtos.PreferencesDto Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return MePreferencesRules.Parse(doc.RootElement);
    }
}
