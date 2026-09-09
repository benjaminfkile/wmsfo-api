namespace Wmsfo.Migrate;

// sql.md 15.10: the tool reads the raw bytes of every referenced legacy logo
// from the legacy bucket. Abstracted so the integration test drives it from a
// dictionary while the CLI is free to plug an S3 client in.
public interface ILegacyLogoSource
{
    Task<byte[]?> GetAsync(string legacyKey, CancellationToken ct);
}

public sealed class InMemoryLegacyLogoSource : ILegacyLogoSource
{
    private readonly Dictionary<string, byte[]> _map;
    public InMemoryLegacyLogoSource(IDictionary<string, byte[]> map)
    {
        _map = new Dictionary<string, byte[]>(map, StringComparer.Ordinal);
    }

    public Task<byte[]?> GetAsync(string legacyKey, CancellationToken ct)
    {
        _map.TryGetValue(legacyKey, out var bytes);
        return Task.FromResult<byte[]?>(bytes);
    }
}

// Reads a legacy logo from a directory on disk, keyed by the relative legacy
// key. Used by the integration test with a directory populated with a couple
// of raster fixtures.
public sealed class FileSystemLegacyLogoSource : ILegacyLogoSource
{
    private readonly string _root;
    public FileSystemLegacyLogoSource(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public async Task<byte[]?> GetAsync(string legacyKey, CancellationToken ct)
    {
        var path = Path.Combine(_root, legacyKey.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, ct);
    }
}
