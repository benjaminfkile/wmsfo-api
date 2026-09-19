using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Logging;

namespace Wmsfo.Api.Email;

// contracts 4.5 Email quota (Admin) and 7.8: reads SES v2 GetAccount through the
// instance role. The three SendQuota numbers are cached in the node's memory for
// 60 s so a panel refresh does not pound SES; an unavailable answer (SES error
// or WMSFO_SES_DRY_RUN=true) is cached for 10 s only so the next attempt is
// close behind. A max24HourSend of -1 in the SES response is null on the wire,
// meaning "no limit"; the endpoint turns that into `remaining` null and
// `wouldExceed` false.
public interface IEmailQuotaReader
{
    Task<EmailQuotaReading> ReadAsync(CancellationToken ct);
}

public readonly record struct EmailQuotaReading(
    bool Available,
    double? Max24HourSend,
    double? SentLast24Hours,
    double? MaxSendRate)
{
    public static EmailQuotaReading Unavailable() => new(false, null, null, null);
}

public sealed class EmailQuotaReader : IEmailQuotaReader
{
    private static readonly TimeSpan AvailableTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UnavailableTtl = TimeSpan.FromSeconds(10);

    private readonly IAmazonSimpleEmailServiceV2? _client;
    private readonly TimeProvider _clock;
    private readonly ILogger<EmailQuotaReader> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private EmailQuotaReading _cached = EmailQuotaReading.Unavailable();
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private bool _hasCache;

    public EmailQuotaReader(
        TimeProvider clock,
        ILogger<EmailQuotaReader> logger,
        IAmazonSimpleEmailServiceV2? client = null)
    {
        _clock = clock;
        _logger = logger;
        _client = client;
    }

    public async Task<EmailQuotaReading> ReadAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_hasCache)
        {
            var ttl = _cached.Available ? AvailableTtl : UnavailableTtl;
            if (now - _cachedAt < ttl)
            {
                return _cached;
            }
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = _clock.GetUtcNow();
            if (_hasCache)
            {
                var ttl = _cached.Available ? AvailableTtl : UnavailableTtl;
                if (now - _cachedAt < ttl)
                {
                    return _cached;
                }
            }

            EmailQuotaReading reading;
            if (_client is null)
            {
                reading = EmailQuotaReading.Unavailable();
            }
            else
            {
                try
                {
                    var response = await _client.GetAccountAsync(new GetAccountRequest(), ct).ConfigureAwait(false);
                    var quota = response.SendQuota;
                    double? max = quota?.Max24HourSend is double m && m >= 0 ? m : null;
                    double? sent = quota?.SentLast24Hours;
                    double? rate = quota?.MaxSendRate;
                    reading = new EmailQuotaReading(true, max, sent, rate);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        "SES GetAccount failed: {ExceptionType} {Message}",
                        ex.GetType().Name, ex.Message);
                    reading = EmailQuotaReading.Unavailable();
                }
            }

            _cached = reading;
            _cachedAt = now;
            _hasCache = true;
            return reading;
        }
        finally
        {
            _gate.Release();
        }
    }
}
