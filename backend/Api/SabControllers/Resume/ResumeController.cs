using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.Resume;

public class ResumeController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<ResumeResponse> Resume(CancellationToken ct) =>
        await Resume(new ResumeRequest { NzoIds = [], CancellationToken = ct }, ct).ConfigureAwait(false);

    public async Task<ResumeResponse> Resume(ResumeRequest request, CancellationToken ct)
    {
        if (request.NzoIds.Count > 0)
        {
            await queueManager.ResumeQueueItemsAsync(request.NzoIds, dbClient, ct).ConfigureAwait(false);
            foreach (var id in request.NzoIds)
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{id}|Queued");
            return new ResumeResponse { Status = true };
        }

        await ConfigPersistenceUtil.SetValueAsync(
            dbClient, Config, ConfigKeys.QueuePaused, "false", ct).ConfigureAwait(false);
        queueManager.AwakenQueue();
        return new ResumeResponse { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await ResumeRequest.New(Context).ConfigureAwait(false);
        return Ok(await Resume(request, Context.RequestAborted).ConfigureAwait(false));
    }
}
