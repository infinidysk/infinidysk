using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckQueue;

[ApiController]
[Route("api/get-health-check-queue")]
public class GetHealthCheckQueueController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<GetHealthCheckQueueResponse> GetHealthCheckQueue(GetHealthCheckQueueRequest request)
    {
        var candidates = await HealthCheckService.GetHealthCheckQueueItems(dbClient)
            .Take(request.PageSize * 3)
            .ToListAsync().ConfigureAwait(false);

        // Skip non-media files so the Health UI focuses on playable media.
        var davItems = candidates
            .Where(x => FilenameUtil.IsHealthCheckCandidate(x.Name))
            .Take(request.PageSize)
            .ToList();

        var uncheckedCount = await HealthCheckService.GetHealthCheckQueueItemsQuery(dbClient)
            .Where(x => x.NextHealthCheck == null)
            .CountAsync().ConfigureAwait(false);

        return new GetHealthCheckQueueResponse()
        {
            UncheckedCount = uncheckedCount,
            Items = davItems.Select(x => new GetHealthCheckQueueResponse.HealthCheckQueueItem()
            {
                Id = x.Id.ToString(),
                Name = x.Name,
                Path = x.Path,
                ReleaseDate = x.ReleaseDate,
                LastHealthCheck = x.LastHealthCheck,
                NextHealthCheck = x.NextHealthCheck,
            }).ToList(),
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetHealthCheckQueueRequest(HttpContext);
        var response = await GetHealthCheckQueue(request).ConfigureAwait(false);
        return Ok(response);
    }
}
