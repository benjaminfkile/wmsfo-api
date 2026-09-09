using Amazon;
using Amazon.S3;
using Wmsfo.Api.Objects;
using Wmsfo.Migrate;

// sql.md 15 legacy migration tool. Boots the target S3 object store and a
// legacy S3 logo source, then hands control to LegacyMigrator. The tool exits
// with 0 on success, 2 for a CLI error, 1 for any other failure.

try
{
    MigrateOptions options;
    try
    {
        options = MigrateOptions.Parse(args);
    }
    catch (MigrateArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-2";
    var s3 = new AmazonS3Client(RegionEndpoint.GetBySystemName(region));
    var target = new S3ObjectStore(s3, options.Bucket);

    var legacyBucket = Environment.GetEnvironmentVariable("WMSFO_LEGACY_BUCKET") ?? options.Bucket;
    ILegacyLogoSource logoSource = new S3LegacyLogoSource(s3, legacyBucket);

    var migrator = new LegacyMigrator(options, target, logoSource, Console.Out);
    var summary = await migrator.RunAsync(CancellationToken.None);
    Console.WriteLine();
    Console.WriteLine(summary.DryRun
        ? "dry run complete."
        : $"migration complete. inserted={summary.TotalInserted} skipped={summary.TotalSkipped}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("migration failed: " + ex.Message);
    Console.Error.WriteLine(ex.ToString());
    return 1;
}

// S3-backed legacy logo reader. Lives here because the CLI is the only caller;
// the tests use InMemoryLegacyLogoSource / FileSystemLegacyLogoSource.
file sealed class S3LegacyLogoSource : ILegacyLogoSource
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public S3LegacyLogoSource(IAmazonS3 s3, string bucket)
    {
        _s3 = s3;
        _bucket = bucket;
    }

    public async Task<byte[]?> GetAsync(string legacyKey, CancellationToken ct)
    {
        try
        {
            var response = await _s3.GetObjectAsync(_bucket, legacyKey, ct);
            using var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
