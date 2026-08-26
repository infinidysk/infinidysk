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
        IReadOnlyList<Guid> stillRunning = [];
        if (ids.Count > 0)
        {
            stillRunning = await queueManager.RemoveQueueItemsAsync(ids, dbClient, cancellationToken)
                .ConfigureAwait(false);
        }

        var removedIds = ids.Where(id => !stillRunning.Contains(id)).ToList();
        if (removedIds.Count > 0)
        {
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, string.Join(",", removedIds));
            _ = DavDatabaseContext.RcloneVfsForget(["/nzbs"], cancellationToken);
        }

        return stillRunning;
    }
}
