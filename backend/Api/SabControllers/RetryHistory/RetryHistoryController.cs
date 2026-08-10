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
        var succeeded = new List<string>();
        var failed = new List<RetryHistoryFailedItem>();

        foreach (var nzoId in request.NzoIds)
        {
            try
            {
                var newId = await RetrySingleHistoryItemAsync(nzoId, request.CancellationToken)
                    .ConfigureAwait(false);
                succeeded.Add(newId.ToString());
            }
            catch (BadHttpRequestException e)
            {
                failed.Add(new RetryHistoryFailedItem
                {
                    NzoId = nzoId.ToString(),
                    Error = e.Message,
                });
            }
        }

        if (succeeded.Count == 0 && failed.Count > 0)
            throw new BadHttpRequestException(failed[0].Error);

        var response = new RetryHistoryResponse
        {
            Status = succeeded.Count > 0,
            NzoIds = succeeded.Count > 0 ? succeeded : null,
            Failed = failed.Count > 0 ? failed : null,
        };
        if (succeeded.Count == 1)
            response.NzoId = succeeded[0];

        return response;
    }

    private async Task<Guid> RetrySingleHistoryItemAsync(Guid nzoId, CancellationToken ct)
    {
        var historyItem = await dbClient.Ctx.HistoryItems.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == nzoId, ct)
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
            CancellationToken = ct,
        };

        var addFileController = new AddFileController(
            Context, dbClient, queueManager, Config, websocketManager);
        var addResponse = await addFileController.AddFileAsync(addFileRequest).ConfigureAwait(false);
        if (addResponse.NzoIds.Count == 0)
            throw new BadHttpRequestException("Failed to re-queue NZB.");

        return Guid.Parse(addResponse.NzoIds[0]);
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RetryHistoryRequest.New(Context).ConfigureAwait(false);
        return Ok(await RetryHistoryAsync(request).ConfigureAwait(false));
    }
}
