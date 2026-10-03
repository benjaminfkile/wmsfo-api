using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Media;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Email;

// The layout's {{logoUrl}} and {{siteName}} of an alert email.
public sealed record EmailBrand(string LogoUrl, string SiteName);

// What the resolver reads from the published site settings: `siteName`, and
// the media_asset row `logoMedia` names (null when absent or gone).
public sealed record PublishedEmailSettings(string? SiteName, EmailLogoAsset? Asset);

public sealed record EmailLogoAsset(
    Guid Id, string Kind, string ContentType, string State, string S3Key, DateTimeOffset? ConfirmedAt);

// contracts 7.8: resolves the email logo and site name at send time from the
// published site settings (the newest content_version). When `logoMedia`
// names a ready raster or gif asset, the logo is EmailLogo.DeriveTile of its
// original, written to email/{sha256}.png when that key is absent; the URL is
// cached in memory per (media id, confirmed_at), so each asset is derived
// once per node. The bundled logo is the answer when `logoMedia` is absent,
// names an svg or an asset that is not ready, or the derivation fails; the
// site name is DefaultSiteName when the published one is empty. Never throws
// for a failure of its own: it logs a warning and answers the fallback.
public sealed class EmailLogoResolver
{
    private readonly Func<CancellationToken, Task<PublishedEmailSettings>> _readPublished;
    private readonly string _cdnBaseUrl;
    private readonly IObjectStore _store;
    private readonly EmailLogo _fallback;
    private readonly ILogger<EmailLogoResolver> _logger;

    // Derived URLs per asset; null marks an asset whose bytes do not derive,
    // so it is not decoded again.
    private readonly ConcurrentDictionary<(Guid MediaId, DateTimeOffset? Stamp), string?> _derived = new();

    public EmailLogoResolver(
        WmsfoConnectionStrings connections,
        WmsfoOptions options,
        IObjectStore store,
        EmailTemplates templates,
        ILogger<EmailLogoResolver> logger)
        : this(ct => ReadPublishedAsync(connections, ct), options.CdnBaseUrl, store, templates.Logo, logger)
    {
    }

    // `readPublished` supplies the published settings; `cdnBaseUrl` is the
    // base of a derived logo's URL.
    public EmailLogoResolver(
        Func<CancellationToken, Task<PublishedEmailSettings>> readPublished,
        string cdnBaseUrl,
        IObjectStore store,
        EmailLogo fallback,
        ILogger<EmailLogoResolver> logger)
    {
        _readPublished = readPublished;
        _cdnBaseUrl = cdnBaseUrl;
        _store = store;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<EmailBrand> ResolveAsync(CancellationToken ct)
    {
        PublishedEmailSettings published;
        try
        {
            published = await _readPublished(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "email logo: reading the published site settings failed: {Error}", ex.Message);
            return new EmailBrand(_fallback.Url, EmailTemplates.DefaultSiteName);
        }

        var siteName = string.IsNullOrWhiteSpace(published.SiteName) ? EmailTemplates.DefaultSiteName : published.SiteName;
        var logoUrl = published.Asset is null ? null : await DerivedUrlAsync(published.Asset, ct).ConfigureAwait(false);
        return new EmailBrand(logoUrl ?? _fallback.Url, siteName);
    }

    private async Task<string?> DerivedUrlAsync(EmailLogoAsset asset, CancellationToken ct)
    {
        if (asset.State != "ready" || asset.Kind == "svg") return null;
        var cacheKey = (asset.Id, asset.ConfirmedAt);
        if (_derived.TryGetValue(cacheKey, out var cached)) return cached;

        try
        {
            var original = await _store.GetObjectAsync(asset.S3Key, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"original {asset.S3Key} is missing");

            byte[] tile;
            try
            {
                tile = EmailLogo.DeriveTile(original.Bytes, asset.ContentType);
            }
            catch (MediaDecodeException ex)
            {
                _logger.LogWarning("email logo: media {MediaId} does not derive ({Reason}); the bundled logo is used",
                    asset.Id, ex.Reason);
                _derived[cacheKey] = null;
                return null;
            }

            var key = EmailLogo.KeyFor(tile);
            var head = await _store.HeadObjectAsync(key, ct).ConfigureAwait(false);
            if (head is null)
            {
                await _store.PutObjectAsync(key, tile, EmailLogo.PngContentType, EmailLogo.ImmutableCacheControl,
                    tag: null, ct).ConfigureAwait(false);
            }
            var url = EmailLogo.UrlFor(_cdnBaseUrl, key);
            _derived[cacheKey] = url;
            return url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A store failure is retried on the next resolve.
            _logger.LogWarning(ex, "email logo: deriving media {MediaId} failed: {Error}; the bundled logo is used",
                asset.Id, ex.Message);
            return null;
        }
    }

    private static async Task<PublishedEmailSettings> ReadPublishedAsync(
        WmsfoConnectionStrings connections, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        string? siteName = null;
        string? mediaIdText = null;
        await using (var cmd = new NpgsqlCommand(@"
select document->'settings'->>'siteName', document->'settings'->'logoMedia'->>'mediaId'
from content_version order by id desc limit 1;", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                siteName = reader.IsDBNull(0) ? null : reader.GetString(0);
                mediaIdText = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        if (!Guid.TryParse(mediaIdText, out var mediaId)) return new PublishedEmailSettings(siteName, null);

        await using (var cmd = new NpgsqlCommand(
            "select kind, content_type, state, s3_key, confirmed_at from media_asset where id = $1;", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new PublishedEmailSettings(siteName, null);
            return new PublishedEmailSettings(siteName, new EmailLogoAsset(
                Id: mediaId,
                Kind: reader.GetString(0),
                ContentType: reader.GetString(1),
                State: reader.GetString(2),
                S3Key: reader.GetString(3),
                ConfirmedAt: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        }
    }
}
