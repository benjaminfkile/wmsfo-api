using System.Security.Cryptography;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Email;

// contracts 7.8 / platform.md 1.2: the one image every email shows,
// templates/email/logo.png, served from the CDN at email/{sha256}.png where
// {sha256} is the lowercase hex SHA-256 of the file. The boot migrator calls
// EnsureWrittenAsync, which PUTs the object only when the key is absent, so
// each logo change is written once and an existing key is never overwritten.
public sealed class EmailLogo
{
    public const string FileName = "logo.png";
    public const string CdnPathPrefix = "email/";
    public const string PngContentType = "image/png";
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    private EmailLogo(byte[] bytes, string sha256, string key, string url)
    {
        Bytes = bytes;
        Sha256 = sha256;
        Key = key;
        Url = url;
    }

    public byte[] Bytes { get; }
    public string Sha256 { get; }
    public string Key { get; }

    // WMSFO_CDN_BASE_URL/email/{sha256}.png, the {{logoUrl}} of the layout.
    public string Url { get; }

    public static EmailLogo Load(string templatesDir, string cdnBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(templatesDir);
        ArgumentNullException.ThrowIfNull(cdnBaseUrl);
        var path = Path.Combine(templatesDir, FileName);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Missing email logo: {path}");

        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = CdnPathPrefix + sha + ".png";
        return new EmailLogo(bytes, sha, key, cdnBaseUrl.TrimEnd('/') + "/" + key);
    }

    // PUTs the logo with the immutable cache header when its key is not in the
    // store yet. Returns true when it wrote.
    public async Task<bool> EnsureWrittenAsync(IObjectStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var head = await store.HeadObjectAsync(Key, cancellationToken).ConfigureAwait(false);
        if (head is not null) return false;

        await store.PutObjectAsync(
            Key,
            Bytes,
            PngContentType,
            ImmutableCacheControl,
            tag: null,
            cancellationToken).ConfigureAwait(false);
        return true;
    }
}
