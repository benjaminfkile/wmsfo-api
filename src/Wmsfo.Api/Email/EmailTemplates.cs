using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Wmsfo.Api.Email;

// contracts 7.8 / api.md 13: templates/email/<name>.html and .txt with
// {{token}} substitution. HTML values are HTML-escaped in the HTML body;
// the text body substitutes the raw value. Templates are loaded once at
// boot and validated for the substitutions each template requires.
public sealed class EmailTemplates
{
    private static readonly Regex TokenPattern = new(@"\{\{([a-zA-Z]+)\}\}", RegexOptions.Compiled);

    // The templates the outbox chore fires (contracts 7.8). Alert templates
    // are keyed by status id via TemplateForStatus.
    public const string SubscriptionVerify = "subscription_verify";
    public const string EventPlanned = "event_planned";
    public const string EventScheduled = "event_scheduled";
    public const string EventLive = "event_live";
    public const string EventEnded = "event_ended";
    public const string EventCancelled = "event_cancelled";
    public const string EventMessage = "event_message";
    public const string ContactReceived = "contact_received";

    // Subjects and required tokens per contracts 7.8. Subjects with {{...}}
    // are substituted like the bodies; contact_received includes the
    // sender name and event_message the first 60 characters of the body.
    // Event alert templates each carry a {{customMessage}} paragraph: the
    // admin's custom text when supplied on the status change or announce,
    // otherwise the template's stock paragraph.
    public static readonly IReadOnlyDictionary<string, TemplateSpec> Specs =
        new Dictionary<string, TemplateSpec>(StringComparer.Ordinal)
        {
            [SubscriptionVerify] = new("Confirm your Santa tracker alerts",
                RequiredTokens: ImmutableArray.Create("verifyUrl", "siteUrl"),
                SubjectTokens: ImmutableArray<string>.Empty),
            [EventPlanned] = new("Santa's flight is on the calendar",
                RequiredTokens: ImmutableArray.Create("customMessage", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray<string>.Empty),
            [EventScheduled] = new("Santa's flight is scheduled",
                RequiredTokens: ImmutableArray.Create("customMessage", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray<string>.Empty),
            [EventLive] = new("Santa just lifted off",
                RequiredTokens: ImmutableArray.Create("customMessage", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray<string>.Empty),
            [EventEnded] = new("Santa has landed",
                RequiredTokens: ImmutableArray.Create("customMessage", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray<string>.Empty),
            [EventCancelled] = new("Santa's flight has been cancelled",
                RequiredTokens: ImmutableArray.Create("customMessage", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray<string>.Empty),
            [EventMessage] = new("Santa update: {{messagePreview}}",
                RequiredTokens: ImmutableArray.Create("eventName", "messageBody", "siteUrl", "unsubscribeUrl"),
                SubjectTokens: ImmutableArray.Create("messagePreview")),
            [ContactReceived] = new("Contact form: {{contactName}}",
                RequiredTokens: ImmutableArray.Create("contactName", "contactEmail", "contactMessage"),
                SubjectTokens: ImmutableArray.Create("contactName")),
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
        _ => throw new ArgumentOutOfRangeException(nameof(statusId), statusId, "unknown status id"),
    };

    // The template's stock paragraph, used for {{customMessage}} when the
    // admin did not supply a custom message on the status change / notify.
    public static string StockParagraph(int statusId, string eventName, DateTimeOffset? scheduledAt) => statusId switch
    {
        1 => $"{eventName} is on the calendar. A specific time will be announced when it is known.",
        2 => scheduledAt is null
            ? $"{eventName} is scheduled."
            : $"{eventName} is scheduled. Lift-off is planned for {FormatMountainTime(scheduledAt.Value)} Mountain time.",
        3 => $"{eventName} is live. Watch Santa's flight now.",
        4 => $"{eventName} has ended. Thanks for flying along.",
        5 => $"{eventName} has been cancelled.",
        _ => eventName,
    };

    private readonly ImmutableDictionary<string, LoadedTemplate> _templates;

    private EmailTemplates(ImmutableDictionary<string, LoadedTemplate> templates)
    {
        _templates = templates;
    }

    public IEnumerable<string> Names => _templates.Keys;

    // Load and validate every template under templates/email/. A missing
    // .html/.txt or a template that does not contain each substitution the
    // spec lists throws so boot fails fast.
    public static EmailTemplates Load(string templatesDir)
    {
        if (!Directory.Exists(templatesDir))
        {
            throw new InvalidOperationException($"Email templates directory not found: {templatesDir}");
        }

        var builder = ImmutableDictionary.CreateBuilder<string, LoadedTemplate>(StringComparer.Ordinal);
        foreach (var (name, spec) in Specs)
        {
            var htmlPath = Path.Combine(templatesDir, name + ".html");
            var textPath = Path.Combine(templatesDir, name + ".txt");
            if (!File.Exists(htmlPath))
                throw new InvalidOperationException($"Missing email template: {htmlPath}");
            if (!File.Exists(textPath))
                throw new InvalidOperationException($"Missing email template: {textPath}");

            var htmlBody = File.ReadAllText(htmlPath);
            var textBody = File.ReadAllText(textPath);

            foreach (var required in spec.RequiredTokens)
            {
                var marker = "{{" + required + "}}";
                if (!htmlBody.Contains(marker, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Template {name}.html missing substitution {marker}.");
                if (!textBody.Contains(marker, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Template {name}.txt missing substitution {marker}.");
            }

            builder[name] = new LoadedTemplate(spec.Subject, htmlBody, textBody);
        }
        return new EmailTemplates(builder.ToImmutable());
    }

    // Substitute the tokens into subject, html, and text. `values` MUST
    // include every RequiredToken plus every SubjectToken.
    public RenderedTemplate Render(string name, IReadOnlyDictionary<string, string> values)
    {
        if (!_templates.TryGetValue(name, out var tpl))
            throw new InvalidOperationException($"Unknown email template: {name}");

        var subject = Substitute(tpl.Subject, values, htmlEscape: false);
        var html = Substitute(tpl.Html, values, htmlEscape: true);
        var text = Substitute(tpl.Text, values, htmlEscape: false);
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

    public static string FormatMountainTime(DateTimeOffset when)
    {
        var mountain = MountainTimeZone();
        var local = TimeZoneInfo.ConvertTime(when.ToUniversalTime(), mountain);
        // e.g. Fri, Dec 24 2027 at 5:00 PM MST
        return local.ToString("ddd, MMM d yyyy 'at' h:mm tt zzz", CultureInfo.InvariantCulture);
    }

    private static TimeZoneInfo MountainTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Denver"); }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");
        }
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
        ImmutableArray<string> SubjectTokens);

    public sealed record LoadedTemplate(string Subject, string Html, string Text);

    public sealed record RenderedTemplate(string Subject, string Html, string Text);
}
