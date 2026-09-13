using Wmsfo.Api.Endpoints;

namespace Wmsfo.Api.Tests;

// A33 unit tests: tag minting rules, the fixed bot marker list, and the salted
// IP hash format. The endpoint code exposes these as pure helpers; the
// integration tests cover the SQL side.
public class QrEndpointsUnitTests
{
    [Theory]
    [InlineData(1, "qr-001")]
    [InlineData(9, "qr-009")]
    [InlineData(10, "qr-010")]
    [InlineData(99, "qr-099")]
    [InlineData(100, "qr-100")]
    [InlineData(999, "qr-999")]
    [InlineData(1000, "qr-1000")]
    [InlineData(1234, "qr-1234")]
    public void Format_tag_zero_pads_to_three_digits_then_natural(int n, string expected)
    {
        Assert.Equal(expected, QrEndpoints.FormatTag(n));
    }

    [Fact]
    public void Bot_marker_list_matches_api_md_11b()
    {
        // The fixed list; the order in the source is the canonical order (api.md 11b).
        Assert.Equal(new[]
        {
            "bot", "crawler", "spider", "preview", "facebookexternalhit",
            "Slackbot", "WhatsApp", "Twitterbot", "LinkedInBot", "HeadlessChrome",
        }, QrEndpoints.BotMarkers);
    }

    [Theory]
    [InlineData("Mozilla/5.0 Googlebot/2.1")]
    [InlineData("SomeCrawler/1.0")]
    [InlineData("Slackbot-LinkExpanding")]
    [InlineData("WhatsApp/2.23.24.85 A")]
    [InlineData("HeadlessChrome/117")]
    [InlineData("facebookexternalhit/1.1")]
    public void Is_bot_matches_a_marker(string userAgent)
    {
        Assert.True(QrEndpoints.IsBotUserAgent(userAgent));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) Gecko Firefox/121.0")]
    [InlineData("Chrome/119.0.6045.199")]
    [InlineData(null)]
    [InlineData("")]
    public void Is_bot_is_false_for_normal_ua(string? userAgent)
    {
        Assert.False(QrEndpoints.IsBotUserAgent(userAgent));
    }

    [Fact]
    public void Ip_hash_is_deterministic_lowercase_hex_and_uses_salt()
    {
        // Salted IP hash: sha256(salt || client IP) as lowercase hex.
        var a = QrEndpoints.ComputeIpHash("salt", "10.0.0.1");
        var b = QrEndpoints.ComputeIpHash("salt", "10.0.0.1");
        var c = QrEndpoints.ComputeIpHash("other", "10.0.0.1");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(64, a.Length);
        Assert.All(a, ch => Assert.True((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')));
    }
}
