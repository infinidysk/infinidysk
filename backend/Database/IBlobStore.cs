using System.Runtime.CompilerServices;

namespace NzbWebDAV.Database;

/// <summary>
/// On-disk blob storage under <c>CONFIG_PATH/blobs</c>. One DI singleton owns
/// the cache and filesystem; call sites should prefer this over the static
/// <see cref="BlobStore"/> facade.
/// </summary>
public interface IBlobStore
{
    [OverloadResolutionPriority(1)]
    Task WriteBlob(Guid id, Stream stream, CancellationToken cancellationToken = default);
    Task WriteBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default);
    Stream? ReadBlob(Guid id);
    Task<T?> ReadBlob<T>(Guid id);
    bool Delete(Guid id);
}
