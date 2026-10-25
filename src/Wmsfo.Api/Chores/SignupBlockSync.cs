using Microsoft.Extensions.Logging;
using Npgsql;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Chores;

// api.md 13 / sql.md 9.9: every 5 minutes on the leader, read every pattern
// oldest first and hand them to the publisher, so a failed publish after a
// write, a mirror run, or a hand edit of the secret is corrected within 5
// minutes. The publisher writes only when the secret differs; one Information
// line only when it wrote.
public sealed class SignupBlockSync
{
    private readonly WmsfoConnectionStrings _connections;
    private readonly ISignupBlockPublisher _publisher;
    private readonly ILogger<SignupBlockSync> _logger;

    public SignupBlockSync(WmsfoConnectionStrings connections, ISignupBlockPublisher publisher,
        ILogger<SignupBlockSync> logger)
    {
        _connections = connections;
        _publisher = publisher;
        _logger = logger;
    }

    // True when the secret was written.
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        List<string> patterns;
        await using (var conn = new NpgsqlConnection(_connections.App))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            patterns = await SignupBlocks.ReadPatternsInOrderAsync(conn, ct).ConfigureAwait(false);
        }
        var wrote = await _publisher.PublishAsync(patterns, ct).ConfigureAwait(false);
        if (wrote)
        {
            _logger.LogInformation("signup block sync: the secret was rewritten; patterns={Patterns}", patterns.Count);
        }
        return wrote;
    }
}
