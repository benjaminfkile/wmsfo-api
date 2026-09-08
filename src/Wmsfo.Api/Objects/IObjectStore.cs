namespace Wmsfo.Api.Objects;

// api.md: Objects/IObjectStore.cs — PutObject, DeleteObject, ListPrefix, CopyObjectWithHeaders.
// The S3 and local implementations land with A6. Only the surface IconLibrary needs is stable
// here; the other methods will land beside their first caller.
public interface IObjectStore
{
    Task PutObjectAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        string cacheControl,
        CancellationToken cancellationToken = default);
}
