using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.DeleteWebdavItem;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.Controllers.DeleteWebdavItemPreview;

[ApiController]
[Route("api/delete-webdav-item-preview")]
public class DeleteWebdavItemPreviewController(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var healthCheckResultId = HttpContext.Request.Query["healthCheckResultId"].FirstOrDefault();
        if (configManager.IsEnforceReadonlyWebdavEnabled() && healthCheckResultId is null)
            return StatusCode(403, new DeleteWebdavItemPreviewResponse
            {
                Status = false,
                Error = "WebDAV is read-only. Disable 'Enforce Read-Only' in Settings → WebDAV."
            });

        var path = HttpContext.Request.Query["path"].FirstOrDefault();
        if (string.IsNullOrEmpty(path))
            return BadRequest(new DeleteWebdavItemPreviewResponse
            {
                Status = false,
                Error = "path is required"
            });
        var ct = HttpContext.RequestAborted;

        var item = await DeleteWebdavItemSupport.ResolvePathAsync(dbClient, path, ct)
            .ConfigureAwait(false);
        if (item is null)
            return NotFound(new DeleteWebdavItemPreviewResponse { Status = false, Error = "Item not found." });

        if (healthCheckResultId is not null &&
            !await DeleteWebdavItemSupport.IsCurrentAttentionFileAsync(dbClient, item, healthCheckResultId, ct)
                .ConfigureAwait(false))
            return Conflict(new DeleteWebdavItemPreviewResponse
            {
                Status = false,
                Error = "This file no longer matches the selected needs-attention result. Refresh Health before trying again."
            });

        var rootError = DeleteWebdavItemSupport.ValidateDeletableRoot(item.Path);
        if (rootError is not null)
            return BadRequest(new DeleteWebdavItemPreviewResponse { Status = false, Error = rootError });

        if (item.IsProtected())
            return StatusCode(403, new DeleteWebdavItemPreviewResponse
            {
                Status = false,
                Error = "Cannot delete protected item."
            });

        if (DeleteWebdavItemSupport.HasInProgressDownload(
                item.Path, queueManager.GetInProgressQueueItems()))
        {
            return Conflict(new DeleteWebdavItemPreviewResponse
            {
                Status = false,
                Error = "Cannot delete while a matching download is in progress."
            });
        }

        var subtree = await dbClient.GetSubtreeForDeleteAsync(item.Id, ct).ConfigureAwait(false);
        if (subtree.Count == 0)
            return NotFound(new DeleteWebdavItemPreviewResponse { Status = false, Error = "Item not found." });

        var fileCount = subtree.Count(x => x.Type == DavItem.ItemType.UsenetFile);
        var dirCount = subtree.Count(x => x.Type == DavItem.ItemType.Directory);
        var totalBytes = item.Type == DavItem.ItemType.Directory
            ? await dbClient.GetRecursiveSize(item.Id, ct).ConfigureAwait(false)
            : item.FileSize ?? 0;
        var linkedHistoryCount = subtree
            .Where(x => x.HistoryItemId.HasValue)
            .Select(x => x.HistoryItemId!.Value)
            .Distinct()
            .Count();

        return Ok(new DeleteWebdavItemPreviewResponse
        {
            Status = true,
            FileCount = fileCount,
            DirCount = dirCount,
            TotalBytes = totalBytes,
            LinkedHistoryCount = linkedHistoryCount,
        });
    }
}
