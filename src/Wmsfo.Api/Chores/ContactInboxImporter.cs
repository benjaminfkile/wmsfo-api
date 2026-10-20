using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data.Sql;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Chores;

// api.md 13 / contracts 7.6 / sql.md 9.8: the contact inbox import every 60 s.
// Lists `contact-inbox/` (keys in time order), takes the first 100, and per
// object reads it, parses it, and in one transaction checks for the row a
// previous run committed, else inserts contact_message (created_at =
// receivedAt) and the contact.received outbox row; then deletes the object.
// A malformed object is deleted with a Warning naming its key, never its
// contents. A database failure leaves the object for the next run; a delete
// failure after the commit is a Warning, and the next run finds the row and
// inserts nothing. Every object call has a 3 s budget. One log line per run.
public sealed class ContactInboxImporter
{
    public const int BatchSize = 100;
    public static readonly TimeSpan ObjectCallTimeout = TimeSpan.FromSeconds(3);

    private readonly WmsfoConnectionStrings _connections;
    private readonly IObjectStore _store;
    private readonly ILogger<ContactInboxImporter> _logger;

    public ContactInboxImporter(
        WmsfoConnectionStrings connections,
        IObjectStore store,
        ILogger<ContactInboxImporter> logger)
    {
        _connections = connections;
        _store = store;
        _logger = logger;
    }

    public sealed record RunResult(int Listed, int Imported, int AlreadyImported, int Malformed, int Failed);

    public async Task<RunResult> RunOnceAsync(CancellationToken ct)
    {
        var keys = await ListBatchAsync(ct).WaitAsync(ObjectCallTimeout, ct).ConfigureAwait(false);

        int imported = 0, already = 0, malformed = 0, failed = 0;
        foreach (var key in keys)
        {
            ObjectContent? content;
            try
            {
                content = await _store.GetObjectAsync(key, ct).WaitAsync(ObjectCallTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "contact inbox read failed; key={Key}", key);
                failed++;
                continue;
            }
            // Gone since the list: an overlapping leader imported it.
            if (content is null) continue;

            var inbox = ContactInbox.TryParse(content.Bytes);
            if (inbox is null)
            {
                _logger.LogWarning("contact inbox object malformed, deleted; key={Key}", key);
                malformed++;
                await DeleteAsync(key, ct).ConfigureAwait(false);
                continue;
            }

            bool inserted;
            try
            {
                inserted = await ImportAsync(inbox, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "contact inbox import failed, kept for the next run; key={Key}", key);
                failed++;
                continue;
            }
            if (inserted) imported++;
            else already++;
            await DeleteAsync(key, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "contact inbox import: listed={Listed} imported={Imported} alreadyImported={AlreadyImported} malformed={Malformed} failed={Failed}",
            keys.Count, imported, already, malformed, failed);
        return new RunResult(keys.Count, imported, already, malformed, failed);
    }

    // The first BatchSize keys under the prefix, in the store's key order.
    private async Task<List<string>> ListBatchAsync(CancellationToken ct)
    {
        var keys = new List<string>(BatchSize);
        await foreach (var entry in _store.ListPrefixAsync(ContactInbox.Prefix, ct).ConfigureAwait(false))
        {
            keys.Add(entry.Key);
            if (keys.Count == BatchSize) break;
        }
        return keys;
    }

    // sql.md 8.13 in one transaction; false when a previous run committed
    // the row already (same email and created_at), which inserts nothing.
    private async Task<bool> ImportAsync(ContactInboxObject inbox, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var check = new NpgsqlCommand(TransactionRecipes.ContactMessageImported, conn, tx))
        {
            check.Parameters.Add(new NpgsqlParameter("email", NpgsqlDbType.Text) { Value = inbox.Email });
            check.Parameters.Add(new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz) { Value = inbox.ReceivedAt });
            if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return false;
            }
        }

        long id;
        await using (var insert = new NpgsqlCommand(TransactionRecipes.ContactMessageInsert, conn, tx))
        {
            insert.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = inbox.Name });
            insert.Parameters.Add(new NpgsqlParameter("email", NpgsqlDbType.Text) { Value = inbox.Email });
            insert.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Text) { Value = inbox.Message });
            insert.Parameters.Add(new NpgsqlParameter("client_ip", NpgsqlDbType.Text) { Value = inbox.ClientIp });
            insert.Parameters.Add(new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz) { Value = inbox.ReceivedAt });
            id = (long)(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }
        await using (var outbox = new NpgsqlCommand(TransactionRecipes.OutboxInsertContactReceived, conn, tx))
        {
            outbox.Parameters.Add(new NpgsqlParameter("contact_message_id", NpgsqlDbType.Bigint) { Value = id });
            await outbox.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task DeleteAsync(string key, CancellationToken ct)
    {
        try
        {
            await _store.DeleteObjectAsync(key, ct).WaitAsync(ObjectCallTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "contact inbox delete failed; key={Key}", key);
        }
    }
}
