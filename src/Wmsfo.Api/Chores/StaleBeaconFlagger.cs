using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data.Sql;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.Chores;

// api.md 13 / contracts 7.6 / sql.md 9.4: one UPDATE sets stale_since on
// every beacon that has not sent a heartbeat or a location in the last
// `beacon_stale_after_s` seconds. A heartbeat or a stored location clears
// the flag (see LocationBeaconStamp and HeartbeatUpdate).
public sealed class StaleBeaconFlagger
{
    private readonly WmsfoConnectionStrings _connections;
    private readonly NodeStateService _state;
    private readonly ILogger<StaleBeaconFlagger> _logger;

    public StaleBeaconFlagger(
        WmsfoConnectionStrings connections,
        NodeStateService state,
        ILogger<StaleBeaconFlagger> logger)
    {
        _connections = connections;
        _state = state;
        _logger = logger;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var seconds = _state.Current.Settings.BeaconStaleAfterS;
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(ChoreRecipes.StaleBeaconFlag, conn);
        cmd.Parameters.Add(new NpgsqlParameter("beacon_stale_after_s", NpgsqlDbType.Integer) { Value = seconds });
        var flagged = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (flagged > 0)
        {
            _logger.LogInformation("stale beacons flagged: {Count}", flagged);
        }
        return flagged;
    }
}
