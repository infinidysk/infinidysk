using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

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
        if (request.RepairStatuses is not null)
            itemsQuery = itemsQuery.Where(x => request.RepairStatuses.Contains(x.RepairStatus));

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
