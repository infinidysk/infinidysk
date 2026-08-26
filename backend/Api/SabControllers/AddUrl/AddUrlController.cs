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
        // Owns the shared fetch/ingest deadline created in AddUrlRequest.New.
        using var fetchDeadline = request.FetchDeadlineSource;
        var controller = new AddFileController(Context, dbClient, queueManager, Config, websocketManager);
        try
        {
            var response = await controller.AddFileAsync(request).ConfigureAwait(false);
            return new AddUrlResponse()
            {
                Status = response.Status,
                NzoIds = response.NzoIds,
            };
        }
        catch (OperationCanceledException ex) when (
            fetchDeadline?.IsCancellationRequested == true && !Context.RequestAborted.IsCancellationRequested)
        {
            // The deadline fired while SubmitAsync was copying the response body;
            // surface a fetch failure to the SAB client instead of an aborted request.
            throw new BadHttpRequestException(
                "Failed to fetch the nzb file: the download timed out.", ex);
        }
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await AddUrlRequest.New(Context, Config, hitTracker).ConfigureAwait(false);
        return Ok(await AddUrlAsync(request).ConfigureAwait(false));
    }
}
