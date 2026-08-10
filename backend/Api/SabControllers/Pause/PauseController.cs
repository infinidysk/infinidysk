using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.Pause;

public class PauseController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public Task<PauseResponse> Pause(CancellationToken ct) =>
        Pause(new PauseRequest { NzoIds = [], CancellationToken = ct }, ct);

    public async Task<PauseResponse> Pause(PauseRequest request, CancellationToken ct)
    {
        if (request.NzoIds.Count > 0)
        {
            await queueManager.PauseQueueItemsAsync(request.NzoIds, dbClient, ct).ConfigureAwait(false);
            foreach (var id in request.NzoIds)
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{id}|Paused");
            return new PauseResponse { Status = true };
        }

        await ConfigPersistenceUtil.SetValueAsync(
            dbClient, Config, ConfigKeys.QueuePaused, "true", ct).ConfigureAwait(false);
        return new PauseResponse { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await PauseRequest.New(Context).ConfigureAwait(false);
        return Ok(await Pause(request, Context.RequestAborted).ConfigureAwait(false));
    }
}
