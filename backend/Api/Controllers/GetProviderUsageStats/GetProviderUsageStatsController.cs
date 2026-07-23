using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetProviderUsageStats;

[ApiController]
[Route("api/get-provider-usage-stats")]
public class GetProviderUsageStatsController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<GetProviderUsageStatsResponse> GetProviderUsageStats(GetProviderUsageStatsRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var thirtyDaysAgo = now.AddDays(-30);
        var totalsPromise = dbClient.Ctx.ProviderUsageStats.ToListAsync(request.CancellationToken);
        var dailyBucketsPromise = dbClient.GetProviderUsageStatsDailyAsync(
            thirtyDaysAgo, now, request.CancellationToken);

        return new GetProviderUsageStatsResponse()
        {
            Totals = await totalsPromise.ConfigureAwait(false),
            DailyBuckets = await dailyBucketsPromise.ConfigureAwait(false)
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetProviderUsageStatsRequest(HttpContext);
        var response = await GetProviderUsageStats(request).ConfigureAwait(false);
        return Ok(response);
    }
}
