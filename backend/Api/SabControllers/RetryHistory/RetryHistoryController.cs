using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.RetryHistory;

public class RetryHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<RetryHistoryResponse> RetryHistoryAsync(RetryHistoryRequest request)
    {
        var historyItem = await dbClient.Ctx.HistoryItems.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == request.NzoId, request.CancellationToken)
            .ConfigureAwait(false);

        if (historyItem is null)
            throw new BadHttpRequestException("History item not found.");

        if (historyItem.DownloadStatus != HistoryItem.DownloadStatusOption.Failed)
            throw new BadHttpRequestException("Only failed history items can be retried.");

        if (historyItem.NzbBlobId is null)
            throw new BadHttpRequestException("The NZB file could not be found.");

        var blobStream = BlobStore.ReadBlob(historyItem.NzbBlobId.Value);
        if (blobStream is null)
            throw new BadHttpRequestException("The NZB file could not be found.");

        var addFileRequest = new AddFileRequest
        {
            FileName = historyItem.FileName,
            ContentType = "application/x-nzb",
            NzbFileStream = blobStream,
            Category = historyItem.Category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
            IndexerName = historyItem.IndexerName,
            ContentGroupKey = historyItem.ContentGroupKey,
            CancellationToken = request.CancellationToken,
        };

        var addFileController = new AddFileController(
            Context, dbClient, queueManager, Config, websocketManager);
        var addResponse = await addFileController.AddFileAsync(addFileRequest).ConfigureAwait(false);
        if (addResponse.NzoIds.Count == 0)
            throw new BadHttpRequestException("Failed to re-queue NZB.");

        return new RetryHistoryResponse
        {
            Status = true,
            NzoId = addResponse.NzoIds[0],
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = RetryHistoryRequest.New(Context);
        return Ok(await RetryHistoryAsync(request).ConfigureAwait(false));
    }
}
