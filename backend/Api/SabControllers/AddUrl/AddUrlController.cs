using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.AddUrl;

public class AddUrlController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager,
    IndexerHitTracker hitTracker
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<AddUrlResponse> AddUrlAsync(AddUrlRequest request)
    {
        var controller = new AddFileController(Context, dbClient, queueManager, Config, websocketManager);
        var response = await controller.AddFileAsync(request).ConfigureAwait(false);
        return new AddUrlResponse()
        {
            Status = response.Status,
            NzoIds = response.NzoIds,
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await AddUrlRequest.New(Context, Config, hitTracker).ConfigureAwait(false);
        return Ok(await AddUrlAsync(request).ConfigureAwait(false));
    }
}
