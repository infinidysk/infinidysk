using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.RemoveFromHistory;

public class RemoveFromHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<RemoveFromHistoryResponse> RemoveFromHistory(RemoveFromHistoryRequest request)
    {
        var historyQuery = dbClient.Ctx.HistoryItems.AsNoTracking();
        var ids = request.DeleteAll
            ? await historyQuery.Select(item => item.Id)
                .ToListAsync(request.CancellationToken)
                .ConfigureAwait(false)
            : request.DeleteFailed
                ? await historyQuery
                    .Where(item => item.DownloadStatus == HistoryItem.DownloadStatusOption.Failed)
                    .Select(item => item.Id)
                    .ToListAsync(request.CancellationToken)
                    .ConfigureAwait(false)
                : request.NzoIds;

        if (ids.Count > 0)
        {
            await dbClient.RemoveHistoryItemsAsync(
                    ids, request.DeleteCompletedFiles, request.CancellationToken)
                .ConfigureAwait(false);
        }
        try
        {
            await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.All(e => e.Entity is HistoryItem))
        {
            // Ignore concurrently deleted history rows, then retry the surviving batch.
            var vanishedIds = ex.Entries
                .Select(e => ((HistoryItem)e.Entity).Id)
                .ToHashSet();

            foreach (var entry in ex.Entries)
                entry.State = EntityState.Detached;

            var cleanupEntries = dbClient.Ctx.ChangeTracker.Entries<HistoryCleanupItem>()
                .Where(e => e.State == EntityState.Added && vanishedIds.Contains(e.Entity.Id))
                .ToList();
            foreach (var entry in cleanupEntries)
                entry.State = EntityState.Detached;

            await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
        }
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", ids));
        return new RemoveFromHistoryResponse() { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RemoveFromHistoryRequest.New(Context).ConfigureAwait(false);
        return Ok(await RemoveFromHistory(request).ConfigureAwait(false));
    }
}
