using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.SetQueuePriority;

public class SetQueuePriorityController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<SetQueuePriorityResponse> SetPriority(SetQueuePriorityRequest request)
    {
        if (request.NzoIds.Count == 0)
            return new SetQueuePriorityResponse { Status = true };

        await queueManager.SetQueueItemsPriorityAsync(
            request.NzoIds, request.Priority, dbClient, request.CancellationToken).ConfigureAwait(false);

        var status = request.Priority == QueueItem.PriorityOption.Paused ? "Paused" : "Queued";
        foreach (var id in request.NzoIds)
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{id}|{status}");

        return new SetQueuePriorityResponse { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await SetQueuePriorityRequest.New(Context).ConfigureAwait(false);
        return Ok(await SetPriority(request).ConfigureAwait(false));
    }
}
