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
    public async Task RemoveAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count > 0)
        {
            await queueManager.RemoveQueueItemsAsync(ids, dbClient, cancellationToken)
                .ConfigureAwait(false);
        }

        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, string.Join(",", ids));
        _ = DavDatabaseContext.RcloneVfsForget(["/nzbs"], cancellationToken);
    }
}
