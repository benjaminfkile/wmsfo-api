using System.Text.Json;
using Npgsql;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// Shared helper: reads the object the snapshot row points at from a test
// host's object store, and the current event's alerts from it.
internal static class CurrentSnapshot
{
    public static async Task<JsonDocument> ReadAsync(string connectionString, IObjectStore store)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select s3_key from snapshot where id = 1;", conn);
        var key = (string)(await cmd.ExecuteScalarAsync() ?? "");
        var content = await store.GetObjectAsync(key);
        Assert.NotNull(content);
        return JsonDocument.Parse(content!.Bytes);
    }

    public static async Task<List<JsonElement>> ReadAlertsAsync(string connectionString, IObjectStore store)
    {
        using var doc = await ReadAsync(connectionString, store);
        return doc.RootElement.GetProperty("event").GetProperty("alerts")
            .EnumerateArray().Select(e => e.Clone()).ToList();
    }
}
