using Wmsfo.Api.Email;

namespace Wmsfo.Api.Tests;

// A30 unit tests: template selection by statusId and stock/custom paragraph
// substitution when the outbox payload does not carry a custom message
// (contracts 7.7).
public sealed class EmailStockParagraphTests
{
    private static string TemplatesDir => Path.Combine(ContractsPaths.RepoRoot, "templates", "email");

    [Fact]
    public void Custom_message_is_used_verbatim_when_provided()
    {
        var templates = EmailTemplates.Load(TemplatesDir);
        var custom = "Come out tonight! Santa lifts off at 6:00 PM.";
        var rendered = templates.Render(EmailTemplates.EventLive, new Dictionary<string, string>
        {
            ["customMessage"] = custom,
            ["siteUrl"] = "https://site.example.com",
            ["unsubscribeUrl"] = "https://site.example.com/alerts/unsubscribe?token=wsu_x",
        });
        Assert.Contains(custom, rendered.Text);
        Assert.Contains(custom, rendered.Html);
    }

    [Fact]
    public void Stock_paragraph_falls_back_to_default_when_no_custom_message()
    {
        var templates = EmailTemplates.Load(TemplatesDir);
        // The outbox chore substitutes StockParagraph when the payload's
        // message is null. Mimic that here to prove the template accepts it.
        var stock = EmailTemplates.StockParagraph(3, "Santa Flyover 2027", null);
        var rendered = templates.Render(EmailTemplates.EventLive, new Dictionary<string, string>
        {
            ["customMessage"] = stock,
            ["siteUrl"] = "https://site.example.com",
            ["unsubscribeUrl"] = "https://site.example.com/alerts/unsubscribe?token=wsu_x",
        });
        Assert.Contains("Santa Flyover 2027", rendered.Text);
        Assert.Contains("live", rendered.Text);
    }

    [Fact]
    public void Template_selection_all_five_statuses()
    {
        Assert.Equal("event_planned", EmailTemplates.TemplateForStatus(1));
        Assert.Equal("event_scheduled", EmailTemplates.TemplateForStatus(2));
        Assert.Equal("event_live", EmailTemplates.TemplateForStatus(3));
        Assert.Equal("event_ended", EmailTemplates.TemplateForStatus(4));
        Assert.Equal("event_cancelled", EmailTemplates.TemplateForStatus(5));
    }

    [Fact]
    public void Template_selection_rejects_unknown_status()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EmailTemplates.TemplateForStatus(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmailTemplates.TemplateForStatus(9));
    }
}
