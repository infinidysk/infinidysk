using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.GetHealthCheckQueue;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetActiveHealthChecks;

[ApiController]
[Route("api/get-active-health-checks")]
public class GetActiveHealthChecksController(
    DavDatabaseClient dbClient,
    HealthCheckService healthCheckService
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var activeProgress = healthCheckService.GetActiveHealthCheckProgress();
        var activeIds = activeProgress.Keys.ToArray();
        var davItems = await dbClient.Ctx.Items
            .Where(item => activeIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .ToListAsync(HttpContext.RequestAborted)
            .ConfigureAwait(false);
        var items = davItems.Select(item => new GetHealthCheckQueueResponse.HealthCheckQueueItem
        {
            Id = item.Id.ToString(),
            Name = item.Name,
            Path = item.Path,
            ReleaseDate = item.ReleaseDate,
            LastHealthCheck = item.LastHealthCheck,
            NextHealthCheck = item.NextHealthCheck,
            Progress = activeProgress.TryGetValue(item.Id, out var progress) ? progress : null,
        }).ToList();

        return Ok(new GetActiveHealthChecksResponse { Items = items });
    }
}

public class GetActiveHealthChecksResponse : BaseApiResponse
{
    public List<GetHealthCheckQueueResponse.HealthCheckQueueItem> Items { get; init; } = [];
}