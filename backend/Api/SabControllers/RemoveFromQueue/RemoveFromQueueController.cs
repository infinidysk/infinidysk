using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.RemoveFromQueue;

public class RemoveFromQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<RemoveFromQueueResponse> RemoveFromQueue(RemoveFromQueueRequest request)
    {
        var ids = request.DeleteAll
            ? await dbClient.Ctx.QueueItems.AsNoTracking()
                .Where(item => request.Category == null || item.Category == request.Category)
                .Select(item => item.Id)
                .ToListAsync(request.CancellationToken)
                .ConfigureAwait(false)
            : request.NzoIds;
        if (ids.Count > 0)
        {
            await queueManager.RemoveQueueItemsAsync(ids, dbClient, request.CancellationToken)
                .ConfigureAwait(false);
        }
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, string.Join(",", ids));
        _ = DavDatabaseContext.RcloneVfsForget(["/nzbs"]);
        return new RemoveFromQueueResponse() { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RemoveFromQueueRequest.New(Context).ConfigureAwait(false);
        return Ok(await RemoveFromQueue(request).ConfigureAwait(false));
    }
}
