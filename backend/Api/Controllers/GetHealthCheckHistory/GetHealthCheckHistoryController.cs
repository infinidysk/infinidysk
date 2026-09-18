using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckHistory;

[ApiController]
[Route("api/get-health-check-history")]
public class GetHealthCheckHistoryController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<GetHealthCheckHistoryResponse> GetHealthCheckHistory(GetHealthCheckHistoryRequest request)
    {
        var now = DateTime.UtcNow;
        var tomorrow = now.AddDays(1);
        var thirtyDaysAgo = now.AddDays(-30);
        var stats = await dbClient.GetHealthCheckStatsAsync(
            thirtyDaysAgo,
            tomorrow,
            request.CancellationToken).ConfigureAwait(false);
        var itemsQuery = dbClient.Ctx.HealthCheckResults
            .AsNoTracking();
        if (request.CurrentActionNeeded)
        {
            itemsQuery = itemsQuery
                .Where(item => item.RepairStatus == HealthCheckResult.RepairAction.ActionNeeded)
                .Where(item => dbClient.Ctx.Items.Any(file =>
                    file.Id == item.DavItemId &&
                    file.Type == DavItem.ItemType.UsenetFile &&
                    file.NextHealthCheck != DateTimeOffset.UnixEpoch &&
                    file.NextHealthCheck != HealthCheckService.ForcedRecheckSentinel))
                .Where(item => !dbClient.Ctx.HealthCheckResults.Any(other =>
                    other.DavItemId == item.DavItemId &&
                    (other.CreatedAt > item.CreatedAt ||
                     (other.CreatedAt == item.CreatedAt &&
                      other.RepairStatus != HealthCheckResult.RepairAction.ActionNeeded))))
                .Where(item => item.Id == dbClient.Ctx.HealthCheckResults
                    .Where(other => other.DavItemId == item.DavItemId)
                    .OrderByDescending(other => other.CreatedAt)
                    .ThenByDescending(other => other.Id)
                    .Select(other => other.Id)
                    .First());
        }
        if (request.RepairStatuses is not null)
            itemsQuery = itemsQuery.Where(x => request.RepairStatuses.Contains(x.RepairStatus));
        if (request.Results is not null)
            itemsQuery = itemsQuery.Where(x => request.Results.Contains(x.Result));

        var totalCount = await itemsQuery.CountAsync(request.CancellationToken).ConfigureAwait(false);
        var items = await itemsQuery
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(request.CancellationToken)
            .ConfigureAwait(false);

        return new GetHealthCheckHistoryResponse()
        {
            Stats = stats,
            Items = items,
            TotalCount = totalCount,
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetHealthCheckHistoryRequest(HttpContext);
        var response = await GetHealthCheckHistory(request).ConfigureAwait(false);
        return Ok(response);
    }
}
