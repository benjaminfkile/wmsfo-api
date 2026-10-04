using Wmsfo.Api.Email;

namespace Wmsfo.Api.Tests;

// templates/email/*.html and *.txt exist, contain their required
// substitutions per contracts 7.8, and render correctly inside the shared
// layout. HTML bodies HTML-escape substituted values; text bodies substitute
// raw. The golden renders under templates/email/_golden/ match byte for byte.
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
        Assert.Contains(EmailTemplates.EventPostponed, templates.Names);
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
    [InlineData(EmailTemplates.EventPostponed, "customMessage")]
    [InlineData(EmailTemplates.EventPostponed, "siteUrl")]
    [InlineData(EmailTemplates.EventPostponed, "unsubscribeUrl")]
    [InlineData(EmailTemplates.EventMessage, "messageBody")]
    [InlineData(EmailTemplates.EventMessage, "unsubscribeUrl")]
    [InlineData(EmailTemplates.ContactReceived, "contactName")]
    [InlineData(EmailTemplates.ContactReceived, "contactEmail")]
    [InlineData(EmailTemplates.ContactReceived, "contactMessage")]
    public void Both_html_and_text_bodies_contain_every_required_substitution(string name, string token)
    {
        // The fragment or, for the unsubscribe link, the template's footer link.
        var link = EmailTemplates.Specs[name].Link;
        var htmlPath = Path.Combine(TemplatesDir, name + ".html");
        var textPath = Path.Combine(TemplatesDir, name + ".txt");
        var html = File.ReadAllText(htmlPath) + link?.Html;
        var text = File.ReadAllText(textPath) + link?.Text;
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
    [InlineData(6, EmailTemplates.EventPostponed)]
    public void TemplateForStatus_maps_each_status_to_its_template(int statusId, string expected)
    {
        Assert.Equal(expected, EmailTemplates.TemplateForStatus(statusId));
    }

    [Fact]
    public void StockParagraph_renders_in_america_denver_when_no_zone_is_set()
    {
        var scheduled = new DateTimeOffset(2026, 12, 20, 1, 0, 0, TimeSpan.Zero);
        var stock = EmailTemplates.StockParagraph(2, "Santa 2026", scheduled);
        Assert.Contains("Santa 2026", stock);
        Assert.Contains("Sat, Dec 19 2026 at 6:00 PM (America/Denver)", stock);
        Assert.DoesNotContain("Mountain time", stock);
    }

    [Fact]
    public void StockParagraph_renders_in_the_event_zone_when_set()
    {
        var scheduled = new DateTimeOffset(2026, 12, 20, 1, 0, 0, TimeSpan.Zero);
        var stock = EmailTemplates.StockParagraph(2, "Santa 2026", scheduled, "America/Chicago");
        Assert.Contains("Sat, Dec 19 2026 at 7:00 PM (America/Chicago)", stock);
        Assert.DoesNotContain("America/Denver", stock);
        Assert.DoesNotContain("Mountain time", stock);
    }

    [Fact]
    public void FormatScheduleTime_falls_back_to_america_denver_for_an_unknown_zone()
    {
        var scheduled = new DateTimeOffset(2026, 12, 20, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal("Sat, Dec 19 2026 at 6:00 PM (America/Denver)",
            EmailTemplates.FormatScheduleTime(scheduled, "Nowhere/Nothing"));
        Assert.Equal("Sat, Dec 19 2026 at 6:00 PM (America/Denver)",
            EmailTemplates.FormatScheduleTime(scheduled, null));
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
                new Dictionary<string, string> { ["siteUrl"] = "s" }));
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
        // The layouts, the logo, and every other template have to be present or
        // the load fails on a different file.
        foreach (var file in new[] { EmailTemplates.HtmlLayoutFile, EmailTemplates.TextLayoutFile, "logo.png" })
            File.Copy(Path.Combine(TemplatesDir, file), Path.Combine(tmp.Path, file));
        foreach (var name in new[]
        {
            EmailTemplates.EventPlanned,
            EmailTemplates.EventScheduled,
            EmailTemplates.EventLive,
            EmailTemplates.EventEnded,
            EmailTemplates.EventCancelled,
            EmailTemplates.EventPostponed,
            EmailTemplates.EventMessage,
            EmailTemplates.ContactReceived,
        })
        {
            File.Copy(Path.Combine(TemplatesDir, name + ".html"), Path.Combine(tmp.Path, name + ".html"));
            File.Copy(Path.Combine(TemplatesDir, name + ".txt"), Path.Combine(tmp.Path, name + ".txt"));
        }
        Assert.Throws<InvalidOperationException>(() => EmailTemplates.Load(tmp.Path));
    }

    private const string CdnBase = "https://cdn.example.com";
    private const string SiteBase = "https://site.example.com";

    // Values for every required and subject token, each carrying characters
    // the HTML part must escape.
    private static Dictionary<string, string> TrickyValues(string name)
    {
        var spec = EmailTemplates.Specs[name];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in spec.RequiredTokens.Concat(spec.SubjectTokens))
            values[token] = token == "siteUrl" ? SiteBase
                : token.EndsWith("Url", StringComparison.Ordinal) ? SiteBase + "/" + token + "?a=1&b=2"
                : token + " <b> & \"x\"";
        return values;
    }

    public static IEnumerable<object[]> TemplateNames() =>
        EmailTemplates.Specs.Keys.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(TemplateNames))]
    public void Every_template_renders_inside_the_layout(string name)
    {
        var templates = EmailTemplates.Load(TemplatesDir, CdnBase, SiteBase);
        var spec = EmailTemplates.Specs[name];
        var values = TrickyValues(name);
        var rendered = templates.Render(name, values);
        var logoUrl = templates.Logo.Url;

        // HTML part: the layout frame, the logo, the footer, escaped values.
        Assert.StartsWith("<!DOCTYPE html>", rendered.Html);
        Assert.Contains("<img src=\"" + logoUrl + "\" width=\"64\" height=\"64\"", rendered.Html);
        Assert.Contains("alt=\"Santa Tracker\"", rendered.Html);
        Assert.Equal(1, CountOf(rendered.Html, "<img "));
        Assert.Contains(System.Net.WebUtility.HtmlEncode(spec.FooterReason), rendered.Html);
        Assert.Contains("<title>" + System.Net.WebUtility.HtmlEncode(rendered.Subject) + "</title>", rendered.Html);
        Assert.Contains("href=\"" + SiteBase + "\"", rendered.Html);
        Assert.Equal(1, CountOf(rendered.Html, "<style"));
        Assert.Contains("<style>:root { color-scheme: light only; }</style>", rendered.Html);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(rendered.Html) < 100 * 1024, "under 100 KB");
        Assert.DoesNotContain("<script", rendered.Html);
        Assert.DoesNotContain("{{", rendered.Html);
        Assert.True(CountOf(rendered.Html, "bgcolor=\"#0b6bb5\"") <= 1, "at most one button");
        foreach (var (token, value) in values)
        {
            if (token.EndsWith("Url", StringComparison.Ordinal))
                Assert.Contains(value.Replace("&", "&amp;"), rendered.Html);
            else
            {
                Assert.Contains(System.Net.WebUtility.HtmlEncode(value), rendered.Html);
                Assert.DoesNotContain(value, rendered.Html);
            }
        }
        if (spec.RequiredTokens.Contains("unsubscribeUrl"))
            Assert.Contains("unsubscribe</a>.</p>", rendered.Html);
        if (spec.Pill is { } pill)
            Assert.Contains(pill.Html, rendered.Html);

        // Text part: the same content, raw, with the footer.
        Assert.Contains(spec.FooterReason, rendered.Text);
        Assert.Contains(SiteBase, rendered.Text);
        Assert.DoesNotContain("{{", rendered.Text);
        foreach (var (token, value) in values)
            Assert.Contains(value, spec.SubjectTokens.Contains(token) ? rendered.Subject : rendered.Text);
    }

    [Fact]
    public void The_layout_names_the_site_and_carries_the_resolved_logo()
    {
        var templates = EmailTemplates.Load(TemplatesDir, CdnBase, SiteBase);
        var values = TrickyValues(EmailTemplates.EventLive);
        values["siteName"] = "North Pole & Co";
        values["logoUrl"] = CdnBase + "/email/abc.png";
        var rendered = templates.Render(EmailTemplates.EventLive, values);

        Assert.Contains("<img src=\"" + CdnBase + "/email/abc.png\" width=\"64\"", rendered.Html);
        Assert.DoesNotContain(templates.Logo.Url, rendered.Html);
        Assert.Contains("alt=\"North Pole &amp; Co\"", rendered.Html);
        Assert.Contains("color:#0f1a30;\">North Pole &amp; Co</td>", rendered.Html);
        Assert.Equal(1, CountOf(rendered.Html, "<img "));
        Assert.StartsWith("North Pole & Co\n", rendered.Text);
    }

    [Fact]
    public void The_layout_names_the_default_site_without_a_site_name()
    {
        var templates = EmailTemplates.Load(TemplatesDir, CdnBase, SiteBase);
        var rendered = templates.Render(EmailTemplates.EventLive, TrickyValues(EmailTemplates.EventLive));

        Assert.Contains("color:#0f1a30;\">Santa Tracker</td>", rendered.Html);
        Assert.StartsWith("Santa Tracker\n", rendered.Text);
    }

    [Fact]
    public void Missing_layout_html_fails_load()
    {
        using var tmp = TempDir.Create();
        foreach (var file in Directory.GetFiles(TemplatesDir))
        {
            if (Path.GetFileName(file) == EmailTemplates.HtmlLayoutFile) continue;
            File.Copy(file, Path.Combine(tmp.Path, Path.GetFileName(file)));
        }
        var ex = Assert.Throws<InvalidOperationException>(() => EmailTemplates.Load(tmp.Path));
        Assert.Contains(EmailTemplates.HtmlLayoutFile, ex.Message);
    }

    [Fact]
    public void Logo_key_is_email_sha256_of_logo_png()
    {
        var bytes = File.ReadAllBytes(Path.Combine(TemplatesDir, "logo.png"));
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var templates = EmailTemplates.Load(TemplatesDir, CdnBase + "/", SiteBase);
        Assert.Equal("email/" + sha + ".png", templates.Logo.Key);
        Assert.Equal(CdnBase + "/email/" + sha + ".png", templates.Logo.Url);
    }

    [Fact]
    public async Task Logo_is_written_once_and_never_overwritten()
    {
        using var tmp = TempDir.Create();
        var store = new Wmsfo.Api.Objects.LocalObjectStore(tmp.Path, "http://localhost:5000");
        var logo = EmailTemplates.Load(TemplatesDir, CdnBase, SiteBase).Logo;

        Assert.True(await logo.EnsureWrittenAsync(store));
        var head = await store.HeadObjectAsync(logo.Key);
        Assert.NotNull(head);
        Assert.Equal("image/png", head!.ContentType);
        Assert.Equal("public, max-age=31536000, immutable", head.CacheControl);
        var stored = await store.GetObjectAsync(logo.Key);
        Assert.Equal(logo.Bytes, stored!.Bytes);

        // A second boot finds the key and writes nothing.
        Assert.False(await logo.EnsureWrittenAsync(store));
    }

    [Theory]
    [InlineData(EmailTemplates.SubscriptionVerify)]
    [InlineData(EmailTemplates.EventLive)]
    public void Golden_renders_match_byte_for_byte(string name)
    {
        var goldenDir = Path.Combine(TemplatesDir, "_golden");
        using var inputs = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(goldenDir, "inputs.json")));
        var root = inputs.RootElement;
        var templates = EmailTemplates.Load(TemplatesDir,
            root.GetProperty("cdnBaseUrl").GetString()!, root.GetProperty("siteUrl").GetString()!);
        Assert.Equal(root.GetProperty("logoUrl").GetString(), templates.Logo.Url);

        var render = root.GetProperty("renders").GetProperty(name);
        var spec = EmailTemplates.Specs[name];
        Assert.Equal(render.TryGetProperty("statusPill", out var pill)
                ? new EmailTemplates.StatusPill(pill.GetProperty("label").GetString()!,
                    pill.GetProperty("background").GetString()!, pill.GetProperty("color").GetString()!)
                : null,
            spec.Pill);
        Assert.Equal(render.TryGetProperty("footerLink", out var link)
                ? new EmailTemplates.FooterLink(link.GetProperty("html").GetString()!, link.GetProperty("txt").GetString()!)
                : null,
            spec.Link);
        var values = render.GetProperty("values").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var rendered = templates.Render(name, values);

        Assert.Equal(render.GetProperty("subject").GetString(), rendered.Subject);
        Assert.Equal(render.GetProperty("preheader").GetString(), EmailTemplates.Specs[name].Preheader);
        Assert.Equal(render.GetProperty("footerReason").GetString(), EmailTemplates.Specs[name].FooterReason);
        Assert.Equal(File.ReadAllBytes(Path.Combine(goldenDir, name + ".html")), System.Text.Encoding.UTF8.GetBytes(rendered.Html));
        Assert.Equal(File.ReadAllBytes(Path.Combine(goldenDir, name + ".txt")), System.Text.Encoding.UTF8.GetBytes(rendered.Text));
    }

    [Fact]
    public void The_live_render_is_the_light_banner_layout()
    {
        var templates = EmailTemplates.Load(TemplatesDir, CdnBase, SiteBase);
        var html = templates.Render(EmailTemplates.EventLive, TrickyValues(EmailTemplates.EventLive)).Html;

        // The light-only declarations.
        Assert.Contains("<meta name=\"color-scheme\" content=\"light\" />", html);
        Assert.Contains("<meta name=\"supported-color-schemes\" content=\"light\" />", html);
        Assert.Contains("<style>:root { color-scheme: light only; }</style>", html);
        // The header band, the body, and the footer band, each background by attribute and style.
        Assert.Contains("bgcolor=\"#f5f8fd\" style=\"padding:20px 28px;background-color:#f5f8fd;border-bottom:1px solid #d3ddee;", html);
        Assert.Contains("bgcolor=\"#ffffff\" style=\"padding:28px;padding:clamp(20px, 5vw, 28px);background-color:#ffffff;", html);
        Assert.Contains("bgcolor=\"#eef3fa\" style=\"padding:16px 28px;background-color:#eef3fa;border-top:1px solid #d3ddee;", html);
        // The "Live now" pill in its two colours.
        Assert.Contains("border-radius:999px;background-color:#e3f6ec;color:#1f7f4f;\">Live now</span>", html);
        Assert.Contains("letter-spacing:0.08em;text-transform:uppercase;", html);
        // The star heading, the quoted message, and the round button.
        Assert.Contains("Santa just lifted off <span style=\"color:#8a6210;\">&#9733;</span></h1>", html);
        Assert.Contains("background-color:#f5f8fd;border-left:3px solid #0b6bb5;border-radius:6px;", html);
        Assert.Contains("white-space:pre-wrap;\">customMessage &lt;b&gt;", html);
        Assert.Contains("bgcolor=\"#0b6bb5\" style=\"background-color:#0b6bb5;border-radius:999px;\"", html);
        Assert.Contains("padding:12px 22px;", html);
        // The unsubscribe line sits in the footer band after the reason.
        var footer = html.IndexOf("border-top:1px solid #d3ddee;", StringComparison.Ordinal);
        Assert.True(footer > 0 && html.IndexOf("unsubscribe</a>", StringComparison.Ordinal) > footer);
        Assert.True(html.IndexOf(EmailTemplates.Specs[EmailTemplates.EventLive].FooterReason, StringComparison.Ordinal)
            < html.IndexOf("unsubscribe</a>", StringComparison.Ordinal));
    }

    [Fact]
    public void The_contact_render_carries_no_pill_and_no_footer_link()
    {
        var templates = EmailTemplates.Load(TemplatesDir, CdnBase, SiteBase);
        var rendered = templates.Render(EmailTemplates.ContactReceived, TrickyValues(EmailTemplates.ContactReceived));

        Assert.DoesNotContain(EmailTemplates.StatusPill.Style, rendered.Html);
        Assert.DoesNotContain("<span", rendered.Html);
        Assert.DoesNotContain("&#9733;", rendered.Html);
        Assert.DoesNotContain("unsubscribe", rendered.Html);
        Assert.DoesNotContain("unsubscribe", rendered.Text);
        // The footer reason is followed directly by the site link.
        Assert.EndsWith(EmailTemplates.Specs[EmailTemplates.ContactReceived].FooterReason + "\n" + SiteBase + "\n", rendered.Text);
    }

    [Theory]
    [InlineData(EmailTemplates.SubscriptionVerify, "Confirm", "#e6f0fa", "#0b6bb5")]
    [InlineData(EmailTemplates.EventPlanned, "Planned", "#e6f0fa", "#0b6bb5")]
    [InlineData(EmailTemplates.EventScheduled, "Scheduled", "#e6f0fa", "#0b6bb5")]
    [InlineData(EmailTemplates.EventLive, "Live now", "#e3f6ec", "#1f7f4f")]
    [InlineData(EmailTemplates.EventEnded, "Landed", "#edf1f7", "#5a6885")]
    [InlineData(EmailTemplates.EventCancelled, "Cancelled", "#fbe7e5", "#c2362c")]
    [InlineData(EmailTemplates.EventPostponed, "Postponed", "#f8efd9", "#8a6210")]
    [InlineData(EmailTemplates.EventMessage, "Update", "#e6f0fa", "#0b6bb5")]
    public void Each_template_carries_its_status_pill(string name, string label, string background, string color)
    {
        Assert.Equal(new EmailTemplates.StatusPill(label, background, color), EmailTemplates.Specs[name].Pill);
        var templates = EmailTemplates.Load(TemplatesDir, CdnBase, SiteBase);
        var html = templates.Render(name, TrickyValues(name)).Html;
        Assert.Contains($"background-color:{background};color:{color};\">{label}</span>", html);
        Assert.Equal(name != EmailTemplates.SubscriptionVerify, html.Contains("&#9733;", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_template_loads_with_every_required_token_in_both_parts()
    {
        var templates = EmailTemplates.Load(TemplatesDir, CdnBase, SiteBase);
        Assert.Equal(EmailTemplates.Specs.Keys.Order(), templates.Names.Order());
        foreach (var (name, spec) in EmailTemplates.Specs)
        {
            var rendered = templates.Render(name, TrickyValues(name));
            foreach (var token in spec.RequiredTokens)
            {
                var value = TrickyValues(name)[token];
                Assert.Contains(System.Net.WebUtility.HtmlEncode(value), rendered.Html);
                Assert.Contains(value, rendered.Text);
            }
        }
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
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
