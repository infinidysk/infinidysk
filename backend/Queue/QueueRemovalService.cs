using NzbWebDAV.Database;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Queue;

/// <summary>
/// Queue-item deletion used by SAB and WebDAV watch folders.
/// </summary>
public sealed class QueueRemovalService(
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    WebsocketManager websocketManager)
{
    /// <summary>
    /// Removes the requested items and returns the ids whose workers ignored
    /// cancellation (still running, still queued). Only actually-removed ids are
    /// announced to the frontend.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RemoveAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return [];

        var result = await queueManager.RemoveQueueItemsDetailedAsync(ids, dbClient, cancellationToken)
            .ConfigureAwait(false);

        if (result.RemovedIds.Length > 0)
        {
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, string.Join(",", result.RemovedIds));
            _ = DavDatabaseContext.RcloneVfsForget(["/nzbs"], cancellationToken);
        }

        return result.StillRunningIds;
    }
}
