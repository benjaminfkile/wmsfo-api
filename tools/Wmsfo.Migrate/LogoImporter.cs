using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Media;
using Wmsfo.Api.Objects;

namespace Wmsfo.Migrate;

// sql.md 15.10: the legacy-logo import slice. Runs the API's confirm pipeline
// on the bytes read from the legacy bucket, PUTs the media/{uuid}/... object
// (and raster variants) to the target bucket, and inserts a media_asset row
// with title 'legacy:<legacy_key>' — the natural key that keeps this
// idempotent. Sponsor.logo_media_id is updated last so a failure between the
// media row and the link is caught by the next rerun.
internal sealed class LogoImporter
{
    public const string SponsorLogoTitlePrefix = LegacyMigrator.LegacyMediaTitlePrefix;
    public const string ImmutableCacheControl = LegacyMigrator.ImmutableCacheControl;
    public const string WebpContentType = "image/webp";

    private readonly IObjectStore _target;
    private readonly ILegacyLogoSource _source;
    private readonly MigrateOptions _options;
    private readonly TextWriter _output;

    public LogoImporter(IObjectStore target, ILegacyLogoSource source, MigrateOptions options, TextWriter output)
    {
        _target = target;
        _source = source;
        _options = options;
        _output = output;
    }

    internal async Task<LogoImportResult> ImportAsync(LegacySponsor sponsor, NpgsqlConnection target, CancellationToken ct)
    {
        var legacyKey = sponsor.LogoS3Key!;
        var title = SponsorLogoTitlePrefix + legacyKey;

        // Existing row lookup by natural key.
        Guid? existingId = null;
        await using (var lookup = new NpgsqlCommand(
            "select id from media_asset where title = $1 and state = 'ready' limit 1;", target))
        {
            lookup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = title });
            var r = await lookup.ExecuteScalarAsync(ct);
            if (r is Guid g) existingId = g;
        }
        if (existingId is Guid have)
        {
            var alreadyLinked = await IsLinkedAsync(target, sponsor.Id, have, ct);
            if (alreadyLinked) return new LogoImportResult(LogoImportStatus.AlreadyLinked);
            if (_options.DryRun) return new LogoImportResult(LogoImportStatus.DryRun);
            await LinkSponsorAsync(target, sponsor.Id, have, ct);
            return new LogoImportResult(LogoImportStatus.ExistingRowRelinked);
        }

        var bytes = await _source.GetAsync(legacyKey, ct);
        if (bytes is null)
            return new LogoImportResult(LogoImportStatus.SourceMissing, "not in legacy bucket");

        var sniffed = ImageSniffer.Detect(bytes);
        if (sniffed is null)
            return new LogoImportResult(LogoImportStatus.ValidationFailed, "type not recognized");

        var contentType = sniffed;
        var lastSegment = legacyKey.Split('/').Last();
        if (string.IsNullOrEmpty(lastSegment)) lastSegment = "logo";
        // Normalize extension to what the sanitizer accepts for the sniffed type.
        var candidate = EnsureExtension(lastSegment, contentType);
        var filename = FilenameSanitizer.Sanitize(candidate, contentType);
        if (filename is null)
            return new LogoImportResult(LogoImportStatus.ValidationFailed, "filename cannot be sanitized");

        int? width = null, height = null;
        var variants = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var newId = Guid.NewGuid();
        var key = $"media/{newId}/{filename}";
        var variantEntries = new List<(int Width, byte[] Bytes)>();

        try
        {
            if (string.Equals(contentType, ImageSniffer.Svg, StringComparison.Ordinal))
            {
                var svg = SvgValidator.Validate(bytes);
                if (!svg.IsValid)
                    return new LogoImportResult(LogoImportStatus.ValidationFailed, $"svg rejected: {svg.Reason}");
            }
            else if (string.Equals(contentType, ImageSniffer.Gif, StringComparison.Ordinal))
            {
                var (w, h) = VariantDeriver.DecodeGifDimensions(bytes);
                width = w; height = h;
            }
            else
            {
                var decoded = VariantDeriver.DecodeRaster(bytes);
                width = decoded.Width;
                height = decoded.Height;
                foreach (var (target1, webp) in decoded.Variants)
                {
                    variantEntries.Add((target1, webp));
                    variants[target1.ToString(CultureInfo.InvariantCulture)] = $"media/{newId}/w{target1}.webp";
                }
            }
        }
        catch (MediaDecodeException ex)
        {
            return new LogoImportResult(LogoImportStatus.ValidationFailed, $"decode failed: {ex.Reason}");
        }

        if (_options.DryRun)
        {
            _output.WriteLine($"  dry run: would PUT {key} ({bytes.Length} bytes) and {variants.Count} variant(s)");
            return new LogoImportResult(LogoImportStatus.DryRun);
        }

        // PUT the original bytes (untagged: this is a migration and there is no
        // pending state) with the immutable header. Variants next.
        await _target.PutObjectAsync(key, bytes, contentType, ImmutableCacheControl, tag: null, ct);
        foreach (var (w, webp) in variantEntries)
        {
            var vkey = $"media/{newId}/w{w}.webp";
            await _target.PutObjectAsync(vkey, webp, WebpContentType, ImmutableCacheControl, tag: null, ct);
        }

        var variantJson = JsonSerializer.Serialize(variants, CanonicalJson.Options);
        var sha = CanonicalJson.Sha256Hex(bytes);
        await using (var ins = new NpgsqlCommand(@"
insert into media_asset (id, filename, content_type, kind, state, s3_key, size_bytes,
                         width, height, sha256, variants, alt, title, uploaded_by,
                         created_at, confirmed_at)
values ($1, $2, $3, $4, 'ready', $5, $6, $7, $8, $9, $10::jsonb, $11, $12, $13, now(), now());", target))
        {
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = newId });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = filename });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = contentType });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = KindFor(contentType) });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (long)bytes.Length });
            ins.Parameters.Add(width is int wv
                ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = wv }
                : new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = DBNull.Value });
            ins.Parameters.Add(height is int hv
                ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = hv }
                : new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = sha });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = variantJson });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = sponsor.Name });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = title });
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = LegacyMigrator.MigrationActor });
            await ins.ExecuteNonQueryAsync(ct);
        }
        await LinkSponsorAsync(target, sponsor.Id, newId, ct);
        return new LogoImportResult(LogoImportStatus.Inserted);
    }

    private static async Task<bool> IsLinkedAsync(NpgsqlConnection target, int sponsorId, Guid mediaId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select logo_media_id from sponsor where id = $1;", target);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (long)sponsorId });
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is Guid g && g == mediaId;
    }

    private static async Task LinkSponsorAsync(NpgsqlConnection target, int sponsorId, Guid mediaId, CancellationToken ct)
    {
        await using var upd = new NpgsqlCommand(
            "update sponsor set logo_media_id = $1, updated_at = now() where id = $2;", target);
        upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = mediaId });
        upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (long)sponsorId });
        await upd.ExecuteNonQueryAsync(ct);
    }

    private static string KindFor(string contentType) => contentType switch
    {
        ImageSniffer.Gif => "gif",
        ImageSniffer.Svg => "svg",
        _ => "raster",
    };

    private static string EnsureExtension(string filename, string contentType)
    {
        var dot = filename.LastIndexOf('.');
        var stem = dot > 0 ? filename[..dot] : filename;
        var ext = contentType switch
        {
            ImageSniffer.Png => "png",
            ImageSniffer.Jpeg => "jpg",
            ImageSniffer.Webp => "webp",
            ImageSniffer.Gif => "gif",
            ImageSniffer.Svg => "svg",
            _ => "bin",
        };
        return stem + "." + ext;
    }
}

internal enum LogoImportStatus
{
    Inserted,
    AlreadyLinked,
    ExistingRowRelinked,
    SourceMissing,
    ValidationFailed,
    DryRun,
}

internal readonly record struct LogoImportResult(LogoImportStatus Status, string? Reason = null);
