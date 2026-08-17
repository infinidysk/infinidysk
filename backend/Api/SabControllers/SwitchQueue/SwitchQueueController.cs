using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.SwitchQueue;

public sealed class SwitchQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<SwitchQueueResponse> SwitchAsync(SwitchQueueRequest request)
    {
        var result = await queueManager
            .SwitchQueueItemAsync(request.SourceId, request.Target, dbClient, request.CancellationToken)
            .ConfigureAwait(false);

        if (result.Position >= 0)
            _ = websocketManager.SendMessage(
                WebsocketTopic.QueueOrderChanged, $"{request.SourceId}|{result.Position}");

        return new SwitchQueueResponse
        {
            Result = new SwitchQueueResponse.ResultObject
            {
                Position = result.Position,
                Priority = result.Priority,
            },
        };
    }

    protected override Task<IActionResult> Handle()
    {
        var request = SwitchQueueRequest.New(Context);
        return HandleAsync(request);
    }

    private async Task<IActionResult> HandleAsync(SwitchQueueRequest request) =>
        Ok(await SwitchAsync(request).ConfigureAwait(false));
}
