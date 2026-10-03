using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Wmsfo.Api.Email;

// contracts 7.8 / api.md 13: every email is a body fragment,
// templates/email/<name>.html and .txt, placed as-is at {{content}} of the
// shared layout, templates/email/_layout.html and _layout.txt. Composition
// happens once at boot, and the composed result is validated for the
// substitutions each template requires. Rendering substitutes {{token}}
// values: HTML-escaped in the HTML part, raw in the text part. The layout
// tokens {{subject}}, {{preheader}}, {{logoUrl}}, {{siteName}}, {{siteUrl}},
// and {{footerReason}} are supplied by Render.
public sealed class EmailTemplates
{
    private static readonly Regex TokenPattern = new(@"\{\{([a-zA-Z]+)\}\}", RegexOptions.Compiled);

    public const string HtmlLayoutFile = "_layout.html";
    public const string TextLayoutFile = "_layout.txt";
    public const string ContentMarker = "{{content}}";

    // The layout's {{siteName}} when the values of a render do not carry one.
    public const string DefaultSiteName = "Santa Tracker";

    // Footer reasons: why the reader got the email.
    private const string AlertFooterReason =
        "You are receiving this because you signed up for Santa Tracker alerts at this address.";

    // The templates the outbox chore fires (contracts 7.8). Alert templates
    // are keyed by status id via TemplateForStatus.
    public const string SubscriptionVerify = "subscription_verify";
    public const string EventPlanned = "event_planned";
    public const string EventScheduled = "event_scheduled";
    public const string EventLive = "event_live";
    public const string EventEnded = "event_ended";
    public const string EventCancelled = "event_cancelled";
    public const string EventPostponed = "event_postponed";
    public const string EventMessage = "event_message";
    public const string ContactReceived = "contact_received";

    // Subjects and required tokens per contracts 7.8. Subjects with {{...}}
    // are substituted like the bodies; contact_received includes the
    // sender name and event_message the first 60 characters of the body.
    // Event alert templates each carry a {{customMessage}} paragraph: the
    // admin's custom text when supplied on the status change or announce,
    // otherwise the template's stock paragraph. The preheader is the hidden
    // line inbox previews show; it and the footer reason are substituted
    // like the subject before they reach the layout.
    public static readonly IReadOnlyDictionary<string, TemplateSpec> Specs =
        new Dictionary<string, TemplateSpec>(StringComparer.Ordinal)
        {
            [SubscriptionVerify] = new("Confirm your Santa tracker alerts",
                RequiredTokens: ImmutableArray.Create("verifyUrl", "siteUrl"),
                SubjectTokens: ImmutableArray<string>.Empty,
                Preheader: "One click confirms this address and turns on your Santa Tracker alerts.",
                FooterReason: "You are receiving this because someone asked for Santa Tracker alerts at this address."),
            [EventPlanned] = new("Santa's flight is on the calendar",
                RequiredTokens: ImmutableArray.Create("customMessage", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray<string>.Empty,
                Preheader: "Santa's next flight is on the calendar, and the time will follow.",
                FooterReason: AlertFooterReason),
            [EventScheduled] = new("Santa's flight is scheduled",
                RequiredTokens: ImmutableArray.Create("customMessage", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray<string>.Empty,
                Preheader: "Santa's flight has a lift-off time.",
                FooterReason: AlertFooterReason),
            [EventLive] = new("Santa just lifted off",
                RequiredTokens: ImmutableArray.Create("customMessage", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray<string>.Empty,
                Preheader: "Santa is in the air right now, so open the tracker and follow the flight.",
                FooterReason: AlertFooterReason),
            [EventEnded] = new("Santa has landed",
                RequiredTokens: ImmutableArray.Create("customMessage", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray<string>.Empty,
                Preheader: "Santa's flight is over, and thanks for flying along.",
                FooterReason: AlertFooterReason),
            [EventCancelled] = new("Santa's flight has been cancelled",
                RequiredTokens: ImmutableArray.Create("customMessage", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray<string>.Empty,
                Preheader: "Santa's flight will not go ahead as planned.",
                FooterReason: AlertFooterReason),
            [EventPostponed] = new("Santa's flight is postponed",
                RequiredTokens: ImmutableArray.Create("customMessage", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray<string>.Empty,
                Preheader: "Santa's flight is postponed, and a new time will follow.",
                FooterReason: AlertFooterReason),
            [EventMessage] = new("Santa update: {{messagePreview}}",
                RequiredTokens: ImmutableArray.Create("eventName", "messageBody", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray.Create("messagePreview"),
                Preheader: "A new update from {{eventName}}.",
                FooterReason: AlertFooterReason),
            [ContactReceived] = new("Contact form: {{contactName}}",
                RequiredTokens: ImmutableArray.Create("contactName", "contactEmail", "contactMessage"),
                SubjectTokens: ImmutableArray.Create("contactName"),
                Preheader: "{{contactName}} sent a message through the Santa Tracker contact form.",
                FooterReason: "You are receiving this because this address receives the Santa Tracker contact form."),
        };

    // The template selected for an event.status_changed or
    // event.status_notified payload (contracts 7.7).
    public static string TemplateForStatus(int statusId) => statusId switch
    {
        1 => EventPlanned,
        2 => EventScheduled,
        3 => EventLive,
        4 => EventEnded,
        5 => EventCancelled,
        6 => EventPostponed,
        _ => throw new ArgumentOutOfRangeException(nameof(statusId), statusId, "unknown status id"),
    };

    // The template's stock paragraph, used for {{customMessage}} when the
    // admin did not supply a custom message on the status change / notify.
    // The scheduled time renders in scheduleTimeZone (see FormatScheduleTime).
    public static string StockParagraph(int statusId, string eventName, DateTimeOffset? scheduledAt,
        string? scheduleTimeZone = null) => statusId switch
    {
        1 => $"{eventName} is on the calendar. A specific time will be announced when it is known.",
        2 => scheduledAt is null
            ? $"{eventName} is scheduled."
            : $"{eventName} is scheduled. Lift-off is planned for {FormatScheduleTime(scheduledAt.Value, scheduleTimeZone)}.",
        3 => $"{eventName} is live. Watch Santa's flight now.",
        4 => $"{eventName} has ended. Thanks for flying along.",
        5 => $"{eventName} has been cancelled.",
        6 => $"{eventName} is postponed. A new time will be announced when it is known.",
        _ => eventName,
    };

    private readonly ImmutableDictionary<string, LoadedTemplate> _templates;
    private readonly string _siteUrl;

    private EmailTemplates(ImmutableDictionary<string, LoadedTemplate> templates, EmailLogo logo, string siteUrl)
    {
        _templates = templates;
        Logo = logo;
        _siteUrl = siteUrl;
    }

    public IEnumerable<string> Names => _templates.Keys;

    // The layout's one image; the boot migrator writes it to the CDN.
    public EmailLogo Logo { get; }

    // Load, compose, and validate every template under templates/email/.
    // A missing layout, layout without {{content}}, logo, fragment .html or
    // .txt, or a composed template that does not contain each substitution
    // the spec lists throws so boot fails fast. `cdnBaseUrl` is
    // WMSFO_CDN_BASE_URL (the logo URL's base); `siteUrl` is
    // WMSFO_SITE_BASE_URL, the layout's {{siteUrl}} when the values of a
    // render do not carry one.
    public static EmailTemplates Load(string templatesDir, string cdnBaseUrl = "", string siteUrl = "")
    {
        if (!Directory.Exists(templatesDir))
        {
            throw new InvalidOperationException($"Email templates directory not found: {templatesDir}");
        }

        var htmlLayout = ReadLayout(templatesDir, HtmlLayoutFile);
        var textLayout = ReadLayout(templatesDir, TextLayoutFile);
        var logo = EmailLogo.Load(templatesDir, cdnBaseUrl);

        var builder = ImmutableDictionary.CreateBuilder<string, LoadedTemplate>(StringComparer.Ordinal);
        foreach (var (name, spec) in Specs)
        {
            var htmlPath = Path.Combine(templatesDir, name + ".html");
            var textPath = Path.Combine(templatesDir, name + ".txt");
            if (!File.Exists(htmlPath))
                throw new InvalidOperationException($"Missing email template: {htmlPath}");
            if (!File.Exists(textPath))
                throw new InvalidOperationException($"Missing email template: {textPath}");

            var htmlBody = Compose(htmlLayout, File.ReadAllText(htmlPath));
            var textBody = Compose(textLayout, File.ReadAllText(textPath));

            foreach (var required in spec.RequiredTokens)
            {
                var marker = "{{" + required + "}}";
                if (!htmlBody.Contains(marker, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Template {name}.html missing substitution {marker}.");
                if (!textBody.Contains(marker, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Template {name}.txt missing substitution {marker}.");
            }

            builder[name] = new LoadedTemplate(spec.Subject, spec.Preheader, spec.FooterReason, htmlBody, textBody);
        }
        return new EmailTemplates(builder.ToImmutable(), logo, siteUrl);
    }

    private static string ReadLayout(string templatesDir, string fileName)
    {
        var path = Path.Combine(templatesDir, fileName);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Missing email layout: {path}");
        var layout = File.ReadAllText(path);
        if (!layout.Contains(ContentMarker, StringComparison.Ordinal))
            throw new InvalidOperationException($"Email layout {fileName} missing {ContentMarker}.");
        return layout;
    }

    // The fragment goes in at {{content}} as-is (trusted template text); a
    // trailing newline of the fragment file is dropped so the layout decides
    // the line breaks around it.
    private static string Compose(string layout, string fragment)
    {
        var body = fragment.EndsWith('\n') ? fragment[..^1] : fragment;
        return layout.Replace(ContentMarker, body, StringComparison.Ordinal);
    }

    // Substitute the tokens into subject, html, and text. `values` MUST
    // include every RequiredToken plus every SubjectToken. Render adds the
    // layout tokens: subject, preheader, and footerReason from the spec;
    // logoUrl (the bundled logo), siteName (DefaultSiteName), and siteUrl
    // (the configured site URL), each unless `values` carries it.
    public RenderedTemplate Render(string name, IReadOnlyDictionary<string, string> values)
    {
        if (!_templates.TryGetValue(name, out var tpl))
            throw new InvalidOperationException($"Unknown email template: {name}");

        var subject = Substitute(tpl.Subject, values, htmlEscape: false);
        var all = new Dictionary<string, string>(values, StringComparer.Ordinal)
        {
            ["subject"] = subject,
            ["preheader"] = Substitute(tpl.Preheader, values, htmlEscape: false),
            ["footerReason"] = Substitute(tpl.FooterReason, values, htmlEscape: false),
        };
        all.TryAdd("logoUrl", Logo.Url);
        all.TryAdd("siteName", DefaultSiteName);
        all.TryAdd("siteUrl", _siteUrl);

        var html = Substitute(tpl.Html, all, htmlEscape: true);
        var text = Substitute(tpl.Text, all, htmlEscape: false);
        return new RenderedTemplate(subject, html, text);
    }

    // A message body that CloudWatch reads back. Missing tokens are
    // rejected because we always render from a fully populated dictionary.
    private static string Substitute(string body, IReadOnlyDictionary<string, string> values, bool htmlEscape)
    {
        return TokenPattern.Replace(body, match =>
        {
            var key = match.Groups[1].Value;
            if (!values.TryGetValue(key, out var value))
                throw new InvalidOperationException($"Missing substitution value: {key}");
            return htmlEscape ? WebUtility.HtmlEncode(value) : value;
        });
    }

    public const string DefaultScheduleTimeZone = "America/Denver";

    // The wall time of `value` in the IANA zone `zoneId`, followed by that
    // zone's id in parentheses. A null or unresolvable zone renders in
    // America/Denver. e.g. Sat, Dec 19 2026 at 6:00 PM (America/Denver)
    public static string FormatScheduleTime(DateTimeOffset value, string? zoneId)
    {
        var (zone, id) = ResolveScheduleZone(zoneId);
        var local = TimeZoneInfo.ConvertTime(value.ToUniversalTime(), zone);
        var wall = local.ToString("ddd, MMM d yyyy 'at' h:mm tt", CultureInfo.InvariantCulture);
        return $"{wall} ({id})";
    }

    private static (TimeZoneInfo Zone, string Id) ResolveScheduleZone(string? zoneId)
    {
        if (!string.IsNullOrWhiteSpace(zoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found))
            return (found, zoneId);
        if (TimeZoneInfo.TryFindSystemTimeZoneById(DefaultScheduleTimeZone, out var fallback))
            return (fallback, DefaultScheduleTimeZone);
        return (TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time"), DefaultScheduleTimeZone);
    }

    // First 60 characters of a message body, used as the {{messagePreview}}
    // in the event_message subject.
    public static string SubjectPreview(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length <= 60) return trimmed;
        return trimmed[..60];
    }

    public sealed record TemplateSpec(
        string Subject,
        ImmutableArray<string> RequiredTokens,
        ImmutableArray<string> SubjectTokens,
        string Preheader,
        string FooterReason);

    public sealed record LoadedTemplate(string Subject, string Preheader, string FooterReason, string Html, string Text);

    public sealed record RenderedTemplate(string Subject, string Html, string Text);
}
