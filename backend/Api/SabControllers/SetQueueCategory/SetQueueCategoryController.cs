using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.SetQueueCategory;

public class SetQueueCategoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<SetQueueCategoryResponse> SetCategory(SetQueueCategoryRequest request)
    {
        if (request.NzoIds.Count == 0)
            return new SetQueueCategoryResponse { Status = true };

        var updatedIds = await queueManager.SetQueueItemsCategoryAsync(
            request.NzoIds, request.Category, dbClient, request.CancellationToken).ConfigureAwait(false);

        foreach (var id in updatedIds)
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{id}|Queued");

        return new SetQueueCategoryResponse { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await SetQueueCategoryRequest.New(Context, Config).ConfigureAwait(false);
        return Ok(await SetCategory(request).ConfigureAwait(false));
    }
}
