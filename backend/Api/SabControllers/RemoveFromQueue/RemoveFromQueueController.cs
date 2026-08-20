using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.RemoveFromQueue;

#pragma warning disable CA1311, CA1862 // PostgreSQL translates ToLower to SQL LOWER.
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
            ? await GetQueueItemIdsToRemoveAsync(request).ConfigureAwait(false)
            : request.NzoIds;
        if (ids.Count > 0)
        {
            await queueManager.RemoveQueueItemsAsync(ids, dbClient, request.CancellationToken)
                .ConfigureAwait(false);
        }
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, string.Join(",", ids));
        _ = DavDatabaseContext.RcloneVfsForget(["/nzbs"], request.CancellationToken);
        return new RemoveFromQueueResponse() { Status = true };
    }

    private async Task<List<Guid>> GetQueueItemIdsToRemoveAsync(RemoveFromQueueRequest request)
    {
        var query = dbClient.Ctx.QueueItems.AsNoTracking();
        if (request.Category is not null)
        {
            query = dbClient.Ctx.Database.IsNpgsql()
                ? query.Where(item => item.Category.ToLower() == request.Category.ToLower())
                : query.Where(item => EF.Functions.Collate(item.Category, "NOCASE") == request.Category);
        }

        return await query
            .Select(item => item.Id)
            .ToListAsync(request.CancellationToken)
            .ConfigureAwait(false);
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RemoveFromQueueRequest.New(Context).ConfigureAwait(false);
        return Ok(await RemoveFromQueue(request).ConfigureAwait(false));
    }
}
