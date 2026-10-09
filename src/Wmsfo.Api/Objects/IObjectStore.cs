namespace Wmsfo.Api.Objects;

// api.md 2 / platform.md 1.2: the object store surface every writer in the
// solution uses. Every key prefix that platform.md 1.2 documents is written
// through PutObjectAsync with the exact content type and cache-control the
// caller specifies (the ones platform.md 1.2 lists per prefix). Tagging maps to
// the two documented tags: `state=pending` on presigned uploads and variant
// PUTs, `state=orphaned` on the orphan chore; confirm deletes the tag.
// PresignPut fixes the key, the content type, and the `x-amz-tagging` header,
// per platform.md 1.5 and 1.4, and is signed for 15 minutes.
public interface IObjectStore
{
    Task PutObjectAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        string cacheControl,
        string? tag = null,
        CancellationToken cancellationToken = default);

    Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ObjectListEntry> ListPrefixAsync(
        string prefix,
        CancellationToken cancellationToken = default);

    Task PutObjectTaggingAsync(string key, string tag, CancellationToken cancellationToken = default);

    Task DeleteObjectTaggingAsync(string key, CancellationToken cancellationToken = default);

    Task<string?> GetObjectTaggingAsync(string key, CancellationToken cancellationToken = default);

    Task<ObjectHead?> HeadObjectAsync(string key, CancellationToken cancellationToken = default);

    Task<ObjectContent?> GetObjectAsync(string key, CancellationToken cancellationToken = default);

    string PresignPut(string key, string contentType, string tag);

    // api.md 11.6: the multipart upload of a map package. Start answers the
    // upload id; the completed object carries the content type and the
    // cache header given at start. A store that never holds a map package
    // (the small test stores) leaves these to the defaults, which refuse.
    Task<string> StartMultipartAsync(
        string key,
        string contentType,
        string cacheControl,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("this store has no multipart uploads");

    // A URL the uploader PUTs one part's bytes to, valid for `expires`.
    string PresignUploadPart(string key, string uploadId, int partNumber, TimeSpan expires) =>
        throw new NotSupportedException("this store has no multipart uploads");

    // Joins the listed parts in order into the object; a missing part or a
    // wrong etag throws.
    Task CompleteMultipartAsync(
        string key,
        string uploadId,
        IReadOnlyList<MultipartPart> parts,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("this store has no multipart uploads");

    Task AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("this store has no multipart uploads");

    // Every open upload whose key starts with the prefix.
    Task<IReadOnlyList<MultipartUploadEntry>> ListMultipartUploadsAsync(
        string prefix,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("this store has no multipart uploads");

    // The bytes from `from` to `to` inclusive (fewer when the object is
    // shorter), or null when the key is absent.
    Task<byte[]?> GetObjectRangeAsync(
        string key,
        long from,
        long to,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("this store has no ranged reads");
}

// One uploaded part of a multipart upload as the uploader reports it.
public sealed record MultipartPart(int PartNumber, string ETag);

// One open multipart upload: its object key and upload id.
public sealed record MultipartUploadEntry(string Key, string UploadId);

// Rows returned by ListPrefixAsync. Size mirrors S3's ListObjectsV2 Content.Size
// so callers do not need a second HeadObject for the byte count.
public sealed record ObjectListEntry(string Key, long Size);

// The subset of a HEAD response every caller in the API needs (media confirm at
// api.md 11.3, orphan chore at sql.md 9.4).
public sealed record ObjectHead(long ContentLength, string ContentType, string CacheControl);

// A fully materialized object body. GetObjectAsync only fires on small artifacts
// (media confirm reads the uploaded bytes), so returning a byte[] keeps every
// caller off IDisposable.
public sealed record ObjectContent(byte[] Bytes, string ContentType);

// The tag alphabet the API uses; the bucket lifecycle rules and the orphan
// chore both key on these two exact strings.
public static class ObjectTags
{
    public const string Pending = "state=pending";
    public const string Orphaned = "state=orphaned";
    // Signed for 15 minutes on every presign (platform.md 1.2, task A6).
    public static readonly TimeSpan PresignLifetime = TimeSpan.FromMinutes(15);
}
