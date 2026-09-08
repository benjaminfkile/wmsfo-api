using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;

namespace Wmsfo.Api.Objects;

// api.md 1 / platform.md 1.5: the AWSSDK.S3 implementation used by every
// deploy. Credentials come from the SDK's default chain, which resolves to the
// EC2 instance role on the fleet (through IMDSv2) and to `AWS_PROFILE` locally.
// Every write matches the exact PutObjectRequest shape in platform.md 1.2
// (Content-Type, Cache-Control, no ACL, no metadata, at most the two documented
// tag values). PresignPut signs a PUT for 15 minutes and binds the content type
// and the `x-amz-tagging` header the browser must send.
public sealed class S3ObjectStore : IObjectStore
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public S3ObjectStore(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        _s3 = s3;
        _bucket = bucket;
    }

    public async Task PutObjectAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        string cacheControl,
        string? tag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheControl);

        // MemoryStream copies the array reference (via ToArray) so the caller's
        // memory can be released as soon as the send returns.
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
        };
        request.Headers.CacheControl = cacheControl;
        if (!string.IsNullOrEmpty(tag))
        {
            request.TagSet = ToTagSet(tag);
        }
        await _s3.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ObjectListEntry> ListPrefixAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string? continuationToken = null;
        do
        {
            var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = continuationToken,
            }, cancellationToken).ConfigureAwait(false);

            if (response.S3Objects is not null)
            {
                foreach (var obj in response.S3Objects)
                {
                    yield return new ObjectListEntry(obj.Key, obj.Size.GetValueOrDefault());
                }
            }

            continuationToken = (response.IsTruncated ?? false) ? response.NextContinuationToken : null;
        }
        while (!string.IsNullOrEmpty(continuationToken));
    }

    public async Task PutObjectTaggingAsync(string key, string tag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        await _s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = _bucket,
            Key = key,
            Tagging = new Tagging { TagSet = ToTagSet(tag) },
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteObjectTaggingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest
        {
            BucketName = _bucket,
            Key = key,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetObjectTaggingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var response = await _s3.GetObjectTaggingAsync(new GetObjectTaggingRequest
        {
            BucketName = _bucket,
            Key = key,
        }, cancellationToken).ConfigureAwait(false);
        var set = response.Tagging;
        if (set is null || set.Count == 0) return null;
        var tag = set[0];
        return string.IsNullOrEmpty(tag.Value) ? tag.Key : $"{tag.Key}={tag.Value}";
    }

    public async Task<ObjectHead?> HeadObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        try
        {
            var response = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key,
            }, cancellationToken).ConfigureAwait(false);
            return new ObjectHead(
                response.ContentLength,
                response.Headers.ContentType ?? "",
                response.Headers.CacheControl ?? "");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ObjectContent?> GetObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key,
            }, cancellationToken).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            return new ObjectContent(ms.ToArray(), response.Headers.ContentType ?? "");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public string PresignPut(string key, string contentType, string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(ObjectTags.PresignLifetime),
            ContentType = contentType,
            Protocol = Protocol.HTTPS,
        };
        // Signing x-amz-tagging binds the exact tag string the browser must
        // send; a mismatched header on the PUT fails the signature check.
        request.Headers["x-amz-tagging"] = tag;
        return _s3.GetPreSignedURL(request);
    }

    private static List<Tag> ToTagSet(string tag)
    {
        var eq = tag.IndexOf('=');
        var key = eq < 0 ? tag : tag[..eq];
        var value = eq < 0 ? string.Empty : tag[(eq + 1)..];
        return new List<Tag> { new Tag { Key = key, Value = value } };
    }
}
