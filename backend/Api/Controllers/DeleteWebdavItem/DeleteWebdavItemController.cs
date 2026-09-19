using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.Controllers.DeleteWebdavItem;

[ApiController]
[Route("api/delete-webdav-item")]
public class DeleteWebdavItemController(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    WebsocketManager websocketManager
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var healthCheckResultId = HttpContext.Request.Form["healthCheckResultId"].FirstOrDefault();
        if (configManager.IsEnforceReadonlyWebdavEnabled() && healthCheckResultId is null)
            return StatusCode(403, new BaseApiResponse
            {
                Status = false,
                Error = "WebDAV is read-only. Disable 'Enforce Read-Only' in Settings → WebDAV."
            });

        var path = HttpContext.Request.Form["path"].FirstOrDefault()
                   ?? throw new BadHttpRequestException("path is required");
        var ct = HttpContext.RequestAborted;

        var item = await DeleteWebdavItemSupport.ResolvePathAsync(dbClient, path, ct)
            .ConfigureAwait(false);
        if (item is null) return NotFound(new BaseApiResponse { Status = false, Error = "Item not found." });

        var rootError = DeleteWebdavItemSupport.ValidateDeletableRoot(item.Path);
        if (rootError is not null)
            return BadRequest(new BaseApiResponse { Status = false, Error = rootError });

        if (item.IsProtected())
            return StatusCode(403, new BaseApiResponse { Status = false, Error = "Cannot delete protected item." });

        if (DeleteWebdavItemSupport.HasInProgressDownload(
                item.Path, queueManager.GetInProgressQueueItems()))
        {
            return Conflict(new BaseApiResponse
            {
                Status = false,
                Error = "Cannot delete while a matching download is in progress."
            });
        }

        var subtree = await dbClient.GetSubtreeForDeleteAsync(item.Id, ct).ConfigureAwait(false);
        if (subtree.Count == 0)
            return NotFound(new BaseApiResponse { Status = false, Error = "Item not found." });

        DeletionAuditLog.WarnBulkDelete("api-delete", subtree.Count, $"path={item.Path}");
        var auditItems = subtree
            .Select(x => new DavItem { Id = x.Id, Type = x.Type, Path = x.Path })
            .ToList();
        DeletionAuditLog.RecordBatch("api-delete", auditItems, "admin delete-webdav-item", item.Id);

        await using var transaction = await dbClient.Ctx.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);
        try
        {
            if (healthCheckResultId is not null &&
                !await DeleteWebdavItemSupport.IsCurrentAttentionFileAsync(dbClient, item, healthCheckResultId, ct)
                    .ConfigureAwait(false))
                return Conflict(new BaseApiResponse
                {
                    Status = false,
                    Error = "This file no longer matches the selected needs-attention result. Refresh Health before trying again."
                });

            var subtreeIds = subtree.Select(x => x.Id).ToList();
            foreach (var batch in subtreeIds.ToBatches(500))
            {
                await dbClient.Ctx.Items
                    .Where(x => batch.Contains(x.Id))
                    .ExecuteDeleteAsync(ct)
                    .ConfigureAwait(false);
            }

            var historyIds = subtree
                .Where(x => x.HistoryItemId.HasValue)
                .Select(x => x.HistoryItemId!.Value)
                .Distinct()
                .ToList();
            var deletedDirectoryIds = subtree
                .Where(x => x.Type == DavItem.ItemType.Directory)
                .Select(x => x.Id)
                .ToList();
            var prunedHistoryIds = await dbClient
                .PruneUnreferencedHistoryItemsAsync(
                    historyIds,
                    source: "explore-delete",
                    ct: ct,
                    deletedDirectoryIds: deletedDirectoryIds)
                .ConfigureAwait(false);

            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            if (prunedHistoryIds.Count > 0)
            {
                _ = websocketManager.SendMessage(
                    WebsocketTopic.HistoryItemRemoved,
                    string.Join(",", prunedHistoryIds));
            }

            _ = DavDatabaseContext.RcloneVfsForget(auditItems, ct);
            return Ok(new BaseApiResponse { Status = true });
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }
}
