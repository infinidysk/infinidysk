using NzbWebDAV.Database.Models;

namespace NzbWebDAV.WebDav.Base;

/// <summary>
/// An upstream Usenet stream whose request-scoped contexts and per-stream semaphore
/// are owned by a shared-stream entry rather than <c>Response.OnCompleted</c>.
/// </summary>
public sealed class DetachedStreamLease
{
    public required Stream Stream { get; init; }
    public required IAsyncDisposable Ownership { get; init; }
    public DavItem? DavItem { get; init; }
}

/// <summary>
/// Store items that can open a stream for a shared-entry-owned token without
/// registering cleanup on the current HTTP response.
/// </summary>
public interface IDetachedStreamSource
{
    long FileSize { get; }
    Task<DetachedStreamLease> GetDetachedReadableStreamAsync(CancellationToken cancellationToken);
}
