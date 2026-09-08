using System.Text;
using Wmsfo.Api.Media;

namespace Wmsfo.Api.Tests;

// api.md 11.4 spec, one case per rejection rule.
public class SvgValidatorTests
{
    [Fact]
    public void Accepts_minimal_svg()
    {
        var svg = Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M1 1 L2 2\"/></svg>");
        var result = SvgValidator.Validate(svg);
        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Rejects_non_svg_root()
    {
        var result = SvgValidator.Validate(Utf8("<html><body/></html>"));
        Assert.False(result.IsValid);
        Assert.Equal("root_not_svg", result.Reason);
    }

    [Fact]
    public void Rejects_script_element()
    {
        var svg = Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var result = SvgValidator.Validate(svg);
        Assert.False(result.IsValid);
        Assert.Equal("element_not_allowed", result.Reason);
    }

    [Fact]
    public void Rejects_foreign_object_element()
    {
        var svg = Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject/></svg>");
        var result = SvgValidator.Validate(svg);
        Assert.False(result.IsValid);
        Assert.Equal("element_not_allowed", result.Reason);
    }

    [Theory]
    [InlineData("onclick")]
    [InlineData("onload")]
    [InlineData("onMouseOver")]
    [InlineData("ONERROR")]
    public void Rejects_event_handler_attributes(string attr)
    {
        var svg = Utf8($"<svg xmlns=\"http://www.w3.org/2000/svg\" {attr}=\"x\"/>");
        var result = SvgValidator.Validate(svg);
        Assert.False(result.IsValid);
        Assert.Equal("event_handler_attribute", result.Reason);
    }

    [Theory]
    [InlineData("http://example.com/x")]
    [InlineData("https://example.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("  HTTP://foo")]
    [InlineData(" JAVASCRIPT:alert(1) ")]
    public void Rejects_external_href_on_href_attribute(string value)
    {
        var svg = Utf8($"<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"{value}\"/></svg>");
        var result = SvgValidator.Validate(svg);
        Assert.False(result.IsValid);
        Assert.Equal("external_href", result.Reason);
    }

    [Theory]
    [InlineData("http://example.com/x")]
    [InlineData("https://example.com/x")]
    [InlineData("javascript:alert(1)")]
    public void Rejects_external_href_on_xlink_href(string value)
    {
        var svg = Utf8($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><use xlink:href=\"{value}\"/></svg>");
        var result = SvgValidator.Validate(svg);
        Assert.False(result.IsValid);
        Assert.Equal("external_href", result.Reason);
    }

    [Fact]
    public void Allows_internal_fragment_href()
    {
        var svg = Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\"><use href=\"#gradient\"/></svg>");
        var result = SvgValidator.Validate(svg);
        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Rejects_document_larger_than_one_mib()
    {
        var body = new string('x', 1024 * 1024 + 1);
        var svg = Utf8($"<svg xmlns=\"http://www.w3.org/2000/svg\"><desc>{body}</desc></svg>");
        var result = SvgValidator.Validate(svg);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_dtd_declaration()
    {
        var svg = Utf8("<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        var result = SvgValidator.Validate(svg);
        Assert.False(result.IsValid);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
