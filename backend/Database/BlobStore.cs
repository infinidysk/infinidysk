using System.Runtime.CompilerServices;

namespace NzbWebDAV.Database;

/// <summary>
/// Process-wide blob facade used by existing call sites. Prefer injecting
/// <see cref="IBlobStore"/>. Production assigns the DI singleton via
/// <see cref="Use"/> at startup so static and injected access share one cache.
/// </summary>
public static class BlobStore
{
    private static readonly Lock Gate = new();
    private static IBlobStore? _current;

    internal static IBlobStore Current
    {
        get
        {
            lock (Gate)
                return _current ??= new FileBlobStore();
        }
    }

    public static void Use(IBlobStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (Gate)
            _current = store;
    }

    internal static void ClearIfCurrent(IBlobStore store)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_current, store))
                _current = null;
        }
    }

    [OverloadResolutionPriority(1)]
    public static Task WriteBlob(
        Guid id,
        Stream stream,
        CancellationToken cancellationToken = default)
        => Current.WriteBlob(id, stream, cancellationToken);

    public static Task WriteBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default)
        => Current.WriteBlob(id, blob, cancellationToken);

    public static Stream? ReadBlob(Guid id)
        => Current.ReadBlob(id);

    public static Task<T?> ReadBlob<T>(Guid id)
        => Current.ReadBlob<T>(id);

    public static bool Delete(Guid id)
        => Current.Delete(id);
}
