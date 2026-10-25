using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Auth;

// api.md 4 and 13, platform.md 3.2 and 6.1: the sign-up gate Lambda reads the
// block list from a Secrets Manager secret per environment
// (WMSFO_SIGNUP_BLOCK_SECRET_ID), so a sign-up never waits on the API. The
// table is the truth; the secret is a copy the API writes after every block
// write and the leader's SignupBlockSync rewrites every 5 minutes.
public interface ISignupBlockPublisher
{
    // Writes the patterns as the secret's string. True when it wrote; false
    // when publishing is off, the secret already held the same string, or a
    // call failed (logged at Warning with `wmsfo_signup_block_publish_failed`).
    Task<bool> PublishAsync(IReadOnlyList<string> patterns, CancellationToken ct);
}

// The secret's string is the JSON array of the patterns, lowercased, in the
// order given (the callers pass created_at order). GetSecretValue first, and
// PutSecretValue only when the string differs. Publishing is off when the key
// is empty or there is no client (WMSFO_DEV_STATIC_TOKENS); the constructor
// says so once at Information. Every call has a 5 s budget.
public sealed class SecretsManagerSignupBlockPublisher : ISignupBlockPublisher
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly IAmazonSecretsManager? _secrets;
    private readonly string _secretId;
    private readonly ILogger<SecretsManagerSignupBlockPublisher> _logger;

    public SecretsManagerSignupBlockPublisher(IAmazonSecretsManager? secrets, WmsfoOptions options,
        ILogger<SecretsManagerSignupBlockPublisher> logger)
    {
        _secrets = secrets;
        _secretId = options.SignupBlockSecretId.Trim();
        _logger = logger;
        if (_secretId.Length == 0)
        {
            _logger.LogInformation("signup block publisher: {Key} is empty, publishing is off",
                WmsfoOptions.Keys.SignupBlockSecretId);
        }
        else if (_secrets is null)
        {
            _logger.LogInformation("signup block publisher: no Secrets Manager client, publishing is off");
        }
    }

    public bool Enabled => _secretId.Length > 0 && _secrets is not null;

    public static string Serialize(IReadOnlyList<string> patterns) =>
        JsonSerializer.Serialize(patterns.Select(SignupBlocks.Normalize).ToArray(), CanonicalJson.Options);

    public async Task<bool> PublishAsync(IReadOnlyList<string> patterns, CancellationToken ct)
    {
        if (!Enabled) return false;
        var value = Serialize(patterns);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallTimeout);
            var current = await _secrets!.GetSecretValueAsync(
                new GetSecretValueRequest { SecretId = _secretId }, cts.Token).ConfigureAwait(false);
            if (string.Equals(current.SecretString, value, StringComparison.Ordinal)) return false;
            await _secrets.PutSecretValueAsync(
                new PutSecretValueRequest { SecretId = _secretId, SecretString = value }, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "signup block publish failed ({ExceptionType}: {ExceptionMessage}); marker={Marker}",
                ex.GetType().Name, ex.Message, LogMarkers.SignupBlockPublishFailed);
            return false;
        }
    }
}

// The step after a committed block write (contracts 4.5 People): read every
// pattern and publish them, on its own 10 s budget so a client that hangs up
// after the commit still gets the list published. Nothing here throws; a
// failure is one Warning with `wmsfo_signup_block_publish_failed`, and the
// response stays what the write answered.
public static class SignupBlockPublish
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public static async Task AfterWriteAsync(NpgsqlConnection conn, ISignupBlockPublisher publisher, ILogger logger)
    {
        try
        {
            using var cts = new CancellationTokenSource(Budget);
            var patterns = await SignupBlocks.ReadPatternsInOrderAsync(conn, cts.Token).ConfigureAwait(false);
            await publisher.PublishAsync(patterns, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "signup block publish failed ({ExceptionType}: {ExceptionMessage}); marker={Marker}",
                ex.GetType().Name, ex.Message, LogMarkers.SignupBlockPublishFailed);
        }
    }
}
