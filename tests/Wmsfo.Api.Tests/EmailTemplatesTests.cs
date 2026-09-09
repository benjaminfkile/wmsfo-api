using Wmsfo.Api.Email;

namespace Wmsfo.Api.Tests;

// A16 unit test: templates/email/*.html and *.txt exist, contain their
// required substitutions per contracts 7.8, and render correctly. HTML
// bodies HTML-escape substituted values; text bodies substitute raw.
public sealed class EmailTemplatesTests
{
    private static string TemplatesDir => Path.Combine(ContractsPaths.RepoRoot, "templates", "email");

    [Fact]
    public void Load_succeeds_and_lists_all_five_templates()
    {
        var templates = EmailTemplates.Load(TemplatesDir);
        Assert.Contains(EmailTemplates.SubscriptionVerify, templates.Names);
        Assert.Contains(EmailTemplates.EventScheduled, templates.Names);
        Assert.Contains(EmailTemplates.EventLive, templates.Names);
        Assert.Contains(EmailTemplates.EventMessage, templates.Names);
        Assert.Contains(EmailTemplates.ContactReceived, templates.Names);
    }

    [Theory]
    [InlineData(EmailTemplates.SubscriptionVerify, "verifyUrl")]
    [InlineData(EmailTemplates.SubscriptionVerify, "siteUrl")]
    [InlineData(EmailTemplates.EventScheduled, "eventName")]
    [InlineData(EmailTemplates.EventScheduled, "scheduledAt")]
    [InlineData(EmailTemplates.EventScheduled, "unsubscribeUrl")]
    [InlineData(EmailTemplates.EventLive, "siteUrl")]
    [InlineData(EmailTemplates.EventLive, "unsubscribeUrl")]
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
    public void Substitutes_event_name_and_html_escapes_it_in_html_body()
    {
        var templates = EmailTemplates.Load(TemplatesDir);
        var rendered = templates.Render(EmailTemplates.EventScheduled, new Dictionary<string, string>
        {
            ["eventName"] = "Santa & Co. <2027>",
            ["scheduledAt"] = "Fri, Dec 24",
            ["siteUrl"] = "https://site.example.com",
            ["unsubscribeUrl"] = "https://site.example.com/alerts/unsubscribe?token=wsu_x",
        });
        // Raw text keeps the ampersand and angle brackets.
        Assert.Contains("Santa & Co. <2027>", rendered.Text);
        // HTML escapes them.
        Assert.Contains("Santa &amp; Co. &lt;2027&gt;", rendered.Html);
        Assert.DoesNotContain("Santa & Co. <2027>", rendered.Html);
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
        foreach (var name in new[] { EmailTemplates.EventScheduled, EmailTemplates.EventLive, EmailTemplates.EventMessage, EmailTemplates.ContactReceived })
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
