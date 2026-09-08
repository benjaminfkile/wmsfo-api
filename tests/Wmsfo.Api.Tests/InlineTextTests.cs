using Wmsfo.Api.Content;

namespace Wmsfo.Api.Tests;

// A13: `InlineText.Parse` is the reference grammar for the site's inline renderer
// (contracts 1.3a). Unbalanced markers are text; malformed `{...}` constructs are
// publish-level problems; links and inline icons are extracted for the
// reference checker.
public class InlineTextTests
{
    [Fact]
    public void Plain_text_produces_no_problems_and_no_references()
    {
        var r = InlineText.Parse("hello world");
        Assert.Empty(r.Problems);
        Assert.Empty(r.Links);
        Assert.Empty(r.Icons);
    }

    [Fact]
    public void Bold_italic_and_code_are_recognised_as_text()
    {
        // Unbalanced markers are text, not errors.
        var r = InlineText.Parse("**bold** *italic* `code` and *lone");
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Well_formed_link_is_extracted()
    {
        var r = InlineText.Parse("Read the [manual](https://example.org/x)!");
        var link = Assert.Single(r.Links);
        Assert.Equal("manual", link.Label);
        Assert.Equal("https://example.org/x", link.Href);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Malformed_link_is_treated_as_text_not_error()
    {
        // `[label]` without a `(href)` is not a link; the bracket is text.
        var r = InlineText.Parse("[not a link] here [also");
        Assert.Empty(r.Links);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Library_icon_is_extracted()
    {
        var r = InlineText.Parse("Look here {icon:sleigh}!");
        var icon = Assert.Single(r.Icons);
        Assert.Equal("library", icon.Source);
        Assert.Equal("sleigh", icon.Id);
    }

    [Fact]
    public void Media_icon_is_extracted()
    {
        var r = InlineText.Parse("Custom {icon:media:8c1d5e2a-7b3f-4c9e-9a1d-2f6e8b4c0a11}");
        var icon = Assert.Single(r.Icons);
        Assert.Equal("media", icon.Source);
        Assert.Equal("8c1d5e2a-7b3f-4c9e-9a1d-2f6e8b4c0a11", icon.Id);
    }

    [Fact]
    public void Malformed_icon_is_a_publish_problem()
    {
        var r = InlineText.Parse("Nope {icon:NOT_A_VALID_ID}");
        Assert.NotEmpty(r.Problems);
    }

    [Fact]
    public void Malformed_media_icon_uuid_is_a_publish_problem()
    {
        var r = InlineText.Parse("Bad {icon:media:not-a-uuid}");
        Assert.NotEmpty(r.Problems);
    }

    [Fact]
    public void Known_event_placeholders_are_accepted()
    {
        var r = InlineText.Parse("This is {event:name} in {event:year} at {event:scheduledAt}");
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Unknown_event_placeholder_is_a_publish_problem()
    {
        var r = InlineText.Parse("Where is {event:whatever}?");
        Assert.NotEmpty(r.Problems);
    }

    [Fact]
    public void Non_matching_brace_is_text()
    {
        var r = InlineText.Parse("Random braces {like this} and {a:b} are text.");
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Unclosed_brace_is_text()
    {
        var r = InlineText.Parse("Trailing {icon:sleigh");
        Assert.Empty(r.Problems);
        Assert.Empty(r.Icons);
    }
}
