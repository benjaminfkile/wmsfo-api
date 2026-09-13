using Wmsfo.Api.Email;

namespace Wmsfo.Api.Tests;

// A16 unit test: templates/email/*.html and *.txt exist, contain their
// required substitutions per contracts 7.8, and render correctly. HTML
// bodies HTML-escape substituted values; text bodies substitute raw.
public sealed class EmailTemplatesTests
{
    private static string TemplatesDir => Path.Combine(ContractsPaths.RepoRoot, "templates", "email");

    [Fact]
    public void Load_succeeds_and_lists_every_template()
    {
        var templates = EmailTemplates.Load(TemplatesDir);
        Assert.Contains(EmailTemplates.SubscriptionVerify, templates.Names);
        Assert.Contains(EmailTemplates.EventPlanned, templates.Names);
        Assert.Contains(EmailTemplates.EventScheduled, templates.Names);
        Assert.Contains(EmailTemplates.EventLive, templates.Names);
        Assert.Contains(EmailTemplates.EventEnded, templates.Names);
        Assert.Contains(EmailTemplates.EventCancelled, templates.Names);
        Assert.Contains(EmailTemplates.EventMessage, templates.Names);
        Assert.Contains(EmailTemplates.ContactReceived, templates.Names);
    }

    [Theory]
    [InlineData(EmailTemplates.SubscriptionVerify, "verifyUrl")]
    [InlineData(EmailTemplates.SubscriptionVerify, "siteUrl")]
    [InlineData(EmailTemplates.EventPlanned, "customMessage")]
    [InlineData(EmailTemplates.EventPlanned, "siteUrl")]
    [InlineData(EmailTemplates.EventPlanned, "unsubscribeUrl")]
    [InlineData(EmailTemplates.EventScheduled, "customMessage")]
    [InlineData(EmailTemplates.EventScheduled, "unsubscribeUrl")]
    [InlineData(EmailTemplates.EventLive, "customMessage")]
    [InlineData(EmailTemplates.EventLive, "siteUrl")]
    [InlineData(EmailTemplates.EventLive, "unsubscribeUrl")]
    [InlineData(EmailTemplates.EventEnded, "customMessage")]
    [InlineData(EmailTemplates.EventEnded, "unsubscribeUrl")]
    [InlineData(EmailTemplates.EventCancelled, "customMessage")]
    [InlineData(EmailTemplates.EventCancelled, "unsubscribeUrl")]
    [InlineData(EmailTemplates.EventMessage, "messageBody")]
    [InlineData(EmailTemplates.EventMessage, "unsubscribeUrl")]
    [InlineData(EmailTemplates.ContactReceived, "contactName")]
    [InlineData(EmailTemplates.ContactReceived, "contactEmail")]
    [InlineData(EmailTemplates.ContactReceived, "contactMessage")]
    public void Both_html_and_text_bodies_contain_every_required_substitution(string name, string token)
    {
        var htmlPath = Path.Combine(TemplatesDir, name + ".html");
        var textPath = Path.Combine(TemplatesDir, name + ".txt");
        var html = File.ReadAllText(htmlPath);
        var text = File.ReadAllText(textPath);
        var marker = "{{" + token + "}}";
        Assert.Contains(marker, html);
        Assert.Contains(marker, text);
    }

    [Fact]
    public void Substitutes_verify_url_in_subscription_verify_template()
    {
        var templates = EmailTemplates.Load(TemplatesDir);
        var rendered = templates.Render(EmailTemplates.SubscriptionVerify, new Dictionary<string, string>
        {
            ["verifyUrl"] = "https://site.example.com/alerts/verify?token=wsv_x",
            ["siteUrl"] = "https://site.example.com",
        });
        Assert.Contains("https://site.example.com/alerts/verify?token=wsv_x", rendered.Text);
        Assert.Contains("https://site.example.com/alerts/verify?token=wsv_x", rendered.Html);
        Assert.Equal("Confirm your Santa tracker alerts", rendered.Subject);
    }

    [Fact]
    public void Substitutes_custom_message_and_html_escapes_it_in_html_body()
    {
        var templates = EmailTemplates.Load(TemplatesDir);
        var rendered = templates.Render(EmailTemplates.EventScheduled, new Dictionary<string, string>
        {
            ["customMessage"] = "Santa & Co. <2027> takes off soon.",
            ["siteUrl"] = "https://site.example.com",
            ["unsubscribeUrl"] = "https://site.example.com/alerts/unsubscribe?token=wsu_x",
        });
        // Raw text keeps the ampersand and angle brackets.
        Assert.Contains("Santa & Co. <2027> takes off soon.", rendered.Text);
        // HTML escapes them.
        Assert.Contains("Santa &amp; Co. &lt;2027&gt; takes off soon.", rendered.Html);
        Assert.DoesNotContain("Santa & Co. <2027> takes off soon.", rendered.Html);
    }

    [Theory]
    [InlineData(1, EmailTemplates.EventPlanned)]
    [InlineData(2, EmailTemplates.EventScheduled)]
    [InlineData(3, EmailTemplates.EventLive)]
    [InlineData(4, EmailTemplates.EventEnded)]
    [InlineData(5, EmailTemplates.EventCancelled)]
    public void TemplateForStatus_maps_each_status_to_its_template(int statusId, string expected)
    {
        Assert.Equal(expected, EmailTemplates.TemplateForStatus(statusId));
    }

    [Fact]
    public void StockParagraph_uses_scheduled_time_when_available()
    {
        var scheduled = new DateTimeOffset(2027, 12, 24, 17, 0, 0, TimeSpan.Zero);
        var stock = EmailTemplates.StockParagraph(2, "Santa 2027", scheduled);
        Assert.Contains("Santa 2027", stock);
        Assert.Contains("Mountain time", stock);
    }

    [Fact]
    public void StockParagraph_falls_back_when_no_schedule()
    {
        var stock = EmailTemplates.StockParagraph(2, "Santa 2027", null);
        Assert.Contains("Santa 2027", stock);
        Assert.DoesNotContain("Mountain time", stock);
    }

    [Fact]
    public void Contact_received_subject_carries_sender_name()
    {
        var templates = EmailTemplates.Load(TemplatesDir);
        var rendered = templates.Render(EmailTemplates.ContactReceived, new Dictionary<string, string>
        {
            ["contactName"] = "Jane Doe",
            ["contactEmail"] = "jane@example.com",
            ["contactMessage"] = "Hi there.",
        });
        Assert.Equal("Contact form: Jane Doe", rendered.Subject);
        Assert.Contains("Jane Doe", rendered.Text);
    }

    [Fact]
    public void Event_message_subject_is_first_60_characters_of_body()
    {
        var templates = EmailTemplates.Load(TemplatesDir);
        var body = new string('x', 120);
        var rendered = templates.Render(EmailTemplates.EventMessage, new Dictionary<string, string>
        {
            ["eventName"] = "Santa 2027",
            ["messageBody"] = body,
            ["messagePreview"] = EmailTemplates.SubjectPreview(body),
            ["siteUrl"] = "https://site.example.com",
            ["unsubscribeUrl"] = "https://site.example.com/alerts/unsubscribe?token=wsu_x",
        });
        Assert.Equal("Santa update: " + new string('x', 60), rendered.Subject);
    }

    [Fact]
    public void Missing_substitution_value_throws()
    {
        var templates = EmailTemplates.Load(TemplatesDir);
        Assert.Throws<InvalidOperationException>(() =>
            templates.Render(EmailTemplates.SubscriptionVerify,
                new Dictionary<string, string> { ["verifyUrl"] = "u" }));
    }

    [Fact]
    public void Missing_template_file_throws_on_load()
    {
        using var tmp = TempDir.Create();
        // Copy in only one file so load fails on the second.
        File.Copy(Path.Combine(TemplatesDir, EmailTemplates.SubscriptionVerify + ".html"),
            Path.Combine(tmp.Path, EmailTemplates.SubscriptionVerify + ".html"));
        Assert.Throws<InvalidOperationException>(() => EmailTemplates.Load(tmp.Path));
    }

    [Fact]
    public void Missing_substitution_marker_throws_on_load()
    {
        using var tmp = TempDir.Create();
        // Both files present but the text body drops the required marker.
        File.Copy(Path.Combine(TemplatesDir, EmailTemplates.SubscriptionVerify + ".html"),
            Path.Combine(tmp.Path, EmailTemplates.SubscriptionVerify + ".html"));
        File.WriteAllText(Path.Combine(tmp.Path, EmailTemplates.SubscriptionVerify + ".txt"),
            "no substitutions at all");
        // And every other template has to be present or the load fails on a different template.
        foreach (var name in new[]
        {
            EmailTemplates.EventPlanned,
            EmailTemplates.EventScheduled,
            EmailTemplates.EventLive,
            EmailTemplates.EventEnded,
            EmailTemplates.EventCancelled,
            EmailTemplates.EventMessage,
            EmailTemplates.ContactReceived,
        })
        {
            File.Copy(Path.Combine(TemplatesDir, name + ".html"), Path.Combine(tmp.Path, name + ".html"));
            File.Copy(Path.Combine(TemplatesDir, name + ".txt"), Path.Combine(tmp.Path, name + ".txt"));
        }
        Assert.Throws<InvalidOperationException>(() => EmailTemplates.Load(tmp.Path));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        private TempDir(string path) { Path = path; }
        public static TempDir Create()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wmsfo-tpl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return new TempDir(dir);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
