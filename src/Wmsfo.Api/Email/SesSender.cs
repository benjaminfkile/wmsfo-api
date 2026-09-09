using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Email;

// api.md 13 / contracts 7.8: SES v2 sender. Builds the message from
// templates/email/<name>.html and .txt with {{token}} substitution
// (HTML-escaped in the HTML body), sets From, To, Subject,
// List-Unsubscribe and List-Unsubscribe-Post on alert templates, Reply-To
// on contact_received, and the configuration set when non-empty. When
// WMSFO_SES_DRY_RUN=true, logs the rendered message instead of sending
// (returns a synthetic ses_message_id).
public interface ISesSender
{
    // Sends an email built from a template. Returns the SES message id.
    Task<string> SendAsync(SesMessage message, CancellationToken ct);
}

public sealed record SesMessage(
    string TemplateName,
    string ToAddress,
    IReadOnlyDictionary<string, string> Values,
    string? ReplyTo = null,
    string? UnsubscribeUrl = null,
    string? UnsubscribeMailto = null);

public sealed class SesSender : ISesSender
{
    private readonly EmailTemplates _templates;
    private readonly WmsfoOptions _options;
    private readonly ILogger<SesSender> _logger;
    private readonly IAmazonSimpleEmailServiceV2? _client;

    public SesSender(
        EmailTemplates templates,
        WmsfoOptions options,
        ILogger<SesSender> logger,
        IAmazonSimpleEmailServiceV2? client = null)
    {
        _templates = templates;
        _options = options;
        _logger = logger;
        _client = client;
    }

    public async Task<string> SendAsync(SesMessage message, CancellationToken ct)
    {
        var rendered = _templates.Render(message.TemplateName, message.Values);

        if (_options.SesDryRun || _client is null)
        {
            _logger.LogInformation(
                "SES DRY RUN: template={Template} to={To} subject={Subject} htmlBytes={HtmlBytes} textBytes={TextBytes}",
                message.TemplateName, message.ToAddress, rendered.Subject,
                System.Text.Encoding.UTF8.GetByteCount(rendered.Html),
                System.Text.Encoding.UTF8.GetByteCount(rendered.Text));
            return "dryrun-" + Guid.NewGuid().ToString("N");
        }

        var request = new SendEmailRequest
        {
            FromEmailAddress = _options.SesFromAddress,
            Destination = new Destination { ToAddresses = new List<string> { message.ToAddress } },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Amazon.SimpleEmailV2.Model.Content { Data = rendered.Subject, Charset = "UTF-8" },
                    Body = new Body
                    {
                        Html = new Amazon.SimpleEmailV2.Model.Content { Data = rendered.Html, Charset = "UTF-8" },
                        Text = new Amazon.SimpleEmailV2.Model.Content { Data = rendered.Text, Charset = "UTF-8" },
                    },
                },
            },
        };
        if (!string.IsNullOrEmpty(message.ReplyTo))
        {
            request.ReplyToAddresses = new List<string> { message.ReplyTo };
        }
        if (!string.IsNullOrEmpty(_options.SesConfigurationSet))
        {
            request.ConfigurationSetName = _options.SesConfigurationSet;
        }
        if (!string.IsNullOrEmpty(message.UnsubscribeUrl))
        {
            var headers = new List<MessageHeader>
            {
                new() { Name = "List-Unsubscribe", Value = $"<{message.UnsubscribeUrl}>" },
                new() { Name = "List-Unsubscribe-Post", Value = "List-Unsubscribe=One-Click" },
            };
            request.Content.Simple.Headers = headers;
        }
        var response = await _client.SendEmailAsync(request, ct).ConfigureAwait(false);
        return response.MessageId ?? "";
    }
}
