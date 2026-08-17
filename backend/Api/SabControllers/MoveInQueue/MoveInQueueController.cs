using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.MoveInQueue;

public class MoveInQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<MoveInQueueResponse> MoveInQueue(MoveInQueueRequest request)
    {
        if (request.NzoIds.Count == 0)
            return new MoveInQueueResponse { Status = true };

        if (!request.MoveToTop)
            throw new BadHttpRequestException("Only move-to-top is supported.");

        var movedIds = await queueManager
            .MoveQueueItemsToTopAsync(request.NzoIds, dbClient, request.CancellationToken)
            .ConfigureAwait(false);

        if (movedIds.Count > 0)
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemMoved, string.Join(",", movedIds));

        return new MoveInQueueResponse { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await MoveInQueueRequest.New(Context).ConfigureAwait(false);
        return Ok(await MoveInQueue(request).ConfigureAwait(false));
    }
}
