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
        if (configManager.IsEnforceReadonlyWebdavEnabled())
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

        var item = await ResolvePathAsync(path, ct).ConfigureAwait(false);
        if (item is null)
            return NotFound(new DeleteWebdavItemPreviewResponse { Status = false, Error = "Item not found." });

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

    private async Task<DavItem?> ResolvePathAsync(string path, CancellationToken ct)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var absolutePath = "/" + string.Join('/', parts.Select(Uri.UnescapeDataString));
        var byPath = await dbClient.GetItemByPathAsync(absolutePath, ct).ConfigureAwait(false);
        if (byPath is not null) return byPath;

        var current = DavItem.Root;
        foreach (var name in parts.Select(Uri.UnescapeDataString))
        {
            var child = await dbClient.GetDirectoryChildAsync(current.Id, name, ct).ConfigureAwait(false);
            if (child is null) return null;
            current = child;
        }
        return current;
    }
}
