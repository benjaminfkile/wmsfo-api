using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.Endpoints;

// Shared plumbing for the /admin/* endpoints (contracts 4.5, api.md 7).
// Reads the audit email from the Cognito ID token (`email` claim), runs the
// [snapshot] frame (api.md 10.2 / sql.md 8.5) around a write, and hands the
// caller the snapshot row's new version so the response can include it.
public static class AdminHelpers
{
    // Every admin write records the caller's email claim (contracts 4.5), or
    // `key:<name>` when the caller is an API key (3.6). A Cognito token
    // without an email claim is not one of ours; refuse it.
    public static string RequireAdminEmail(HttpContext ctx)
    {
        var keyName = ApiKeyAuthenticationHandler.TryGetApiKeyName(ctx.User);
        if (!string.IsNullOrEmpty(keyName))
        {
            return $"key:{keyName}";
        }
        var email = ctx.User.FindFirst(PersonClaims.Email)?.Value;
        if (string.IsNullOrEmpty(email))
        {
            throw new ApiException(StatusCodes.Status401Unauthorized,
                ApiErrorCodes.Unauthenticated, "no email claim");
        }
        return email;
    }

    // Base64url decoder for the cursor token (contracts 4.0, api.md 7): the
    // cursor is the id/seq boundary as a URL-safe base64 string.
    public static long? DecodeLongCursor(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var padded = raw.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            throw new ApiException(StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed, "cursor is malformed");
        }
    }

    public static string EncodeLongCursor(long value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static int ClampLimit(int? raw, int defaultLimit, int max = 500)
    {
        if (raw is null) return defaultLimit;
        if (raw <= 0) return defaultLimit;
        return Math.Min(raw.Value, max);
    }
}

// api.md 10.2 / sql.md 8.5 [snapshot] frame runner. The caller provides the
// write; this class wraps it with `select ... for update` on snapshot,
// BuildAndPutAsync, ApplySnapshotRowUpdateAsync, commit, then fires
// WriteFromStateAsync after commit (do not await; the response does not wait
// for the live-object write).
public sealed class AdminSnapshotTransaction
{
    private readonly SnapshotBuilder _snapshot;
    private readonly LiveObjectWriter _writer;
    private readonly NodeStateService _state;
    private readonly WmsfoConnectionStrings _connections;

    public AdminSnapshotTransaction(
        SnapshotBuilder snapshot,
        LiveObjectWriter writer,
        NodeStateService state,
        WmsfoConnectionStrings connections)
    {
        _snapshot = snapshot;
        _writer = writer;
        _state = state;
        _connections = connections;
    }

    public delegate Task<T> WriteFunc<T>(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct);

    // Runs the caller's write inside the snapshot frame; returns (value, version).
    // A PgsqlException with a mapped constraint becomes the mapped ApiException
    // (sql.md 4.3).
    public async Task<(T Value, long SnapshotVersion)> RunAsync<T>(WriteFunc<T> write, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connections.App);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using (var lockRow = new NpgsqlCommand(
                "select id from snapshot where id = 1 for update;", conn, tx))
            {
                await lockRow.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }

            var value = await write(conn, tx, ct).ConfigureAwait(false);

            var built = await _snapshot.BuildAndPutAsync(conn, tx, ct).ConfigureAwait(false);
            var version = await SnapshotBuilder.ApplySnapshotRowUpdateAsync(conn, tx, built, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

            _state.RecordWroteVersion(version);
            // Fire-and-forget; the response does not wait for the live-object write.
            _ = _writer.WriteFromStateAsync("admin", CancellationToken.None);

            return (value, version);
        }
        catch (PostgresException ex) when (ConstraintErrorMapping.Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    // Overload for a write with no return value (e.g., DELETE handlers).
    public async Task<long> RunAsync(WriteFunc<object?> write, CancellationToken ct)
    {
        var (_, version) = await RunAsync<object?>(write, ct).ConfigureAwait(false);
        return version;
    }
}
