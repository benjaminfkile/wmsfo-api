namespace Wmsfo.Migrate;

// sql.md 15: the tool's CLI. Legacy and new connection strings, the target
// bucket name, the CDN base URL used to build absolute URLs for route rows,
// the event name format (default "Santa Flyover {year}"), and --dry-run for a
// preview run that rolls every transaction back and skips every bucket write.
public sealed record MigrateOptions
{
    public const string DefaultEventNameFormat = "Santa Flyover {year}";

    public required string LegacyConnection { get; init; }
    public required string TargetConnection { get; init; }
    public required string Bucket { get; init; }
    public required string CdnBaseUrl { get; init; }
    public string EventNameFormat { get; init; } = DefaultEventNameFormat;
    public bool DryRun { get; init; }

    // The tz used to bucket year-less legacy timestamps into calendar years,
    // per sql.md 15 ("extract(year from ts at time zone 'America/Denver')").
    public string DenverTimeZoneId { get; init; } = "America/Denver";

    // Optional. When set, the tool assumes a local development store rooted at
    // this path for the target bucket and reads legacy logos from
    // <LegacyLogoDir> as raw files keyed by logo_s3_key. The CLI does not
    // populate these; they exist for the integration test host.
    public string? LocalTargetStoreDir { get; init; }
    public string? LegacyLogoDir { get; init; }

    public string FormatEventName(int year) =>
        EventNameFormat.Replace("{year}", year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    public static MigrateOptions Parse(IReadOnlyList<string> args)
    {
        string? legacy = null, target = null, bucket = null, cdn = null;
        var eventNameFormat = DefaultEventNameFormat;
        var dryRun = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--legacy":
                case "--legacy-conn":
                    legacy = RequireNext(args, ref i, a);
                    break;
                case "--target":
                case "--target-conn":
                    target = RequireNext(args, ref i, a);
                    break;
                case "--bucket":
                    bucket = RequireNext(args, ref i, a);
                    break;
                case "--cdn-base-url":
                    cdn = RequireNext(args, ref i, a);
                    break;
                case "--event-name-format":
                    eventNameFormat = RequireNext(args, ref i, a);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--help":
                case "-h":
                    throw new MigrateArgumentException(HelpText);
                default:
                    throw new MigrateArgumentException($"unknown argument: {a}\n\n{HelpText}");
            }
        }

        if (legacy is null || target is null || bucket is null || cdn is null)
            throw new MigrateArgumentException(HelpText);

        return new MigrateOptions
        {
            LegacyConnection = legacy,
            TargetConnection = target,
            Bucket = bucket,
            CdnBaseUrl = cdn,
            EventNameFormat = eventNameFormat,
            DryRun = dryRun,
        };
    }

    private static string RequireNext(IReadOnlyList<string> args, ref int i, string flag)
    {
        if (i + 1 >= args.Count)
            throw new MigrateArgumentException($"{flag} requires a value");
        return args[++i];
    }

    public const string HelpText =
        """
        Usage: Wmsfo.Migrate --legacy <conn> --target <conn> --bucket <name> --cdn-base-url <url>
                             [--event-name-format "Santa Flyover {year}"] [--dry-run]

        Runs the one-off migration described in docs/sql.md section 15. Idempotent by
        natural key; a second run writes nothing. With --dry-run every write is rolled
        back and no bucket PUTs are made.
        """;
}

public sealed class MigrateArgumentException : Exception
{
    public MigrateArgumentException(string message) : base(message) { }
}
