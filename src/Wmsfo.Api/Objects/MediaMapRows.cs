using System.Text.Json;
using Npgsql;

namespace Wmsfo.Api.Objects;

// The media map read shared by the snapshot builder and the preview (contracts
// 1.3b, sql.md 7): one row per asset with its ready dark version joined in.
public static class MediaMapRows
{
    public const string Select = @"
select m.id, m.s3_key, m.kind, m.width, m.height, m.alt, m.variants, m.dzi_key,
       m.invert_in_dark, d.s3_key, d.variants
from media_asset m
left join media_asset d on d.id = m.dark_media_id and d.state = 'ready'";

    public static (Guid Id, MediaEntry Entry) Read(NpgsqlDataReader reader, string cdn)
    {
        var id = reader.GetGuid(0);
        var dziKey = reader.IsDBNull(7) ? null : reader.GetString(7);
        var entry = new MediaEntry
        {
            Url = cdn + "/" + reader.GetString(1),
            Kind = reader.GetString(2),
            Width = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            Height = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            Alt = reader.GetString(5),
            Variants = ReadVariants(reader.GetString(6), cdn),
            Dzi = dziKey is null ? null : cdn + "/" + dziKey,
            InvertInDark = reader.GetBoolean(8),
        };
        if (!reader.IsDBNull(9))
        {
            entry.Dark = new MediaDarkEntry
            {
                Url = cdn + "/" + reader.GetString(9),
                Variants = ReadVariants(reader.GetString(10), cdn),
            };
        }
        return (id, entry);
    }

    private static SortedDictionary<string, string> ReadVariants(string json, string cdn)
    {
        var variants = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateObject())
        {
            variants[e.Name] = cdn + "/" + e.Value.GetString();
        }
        return variants;
    }
}
