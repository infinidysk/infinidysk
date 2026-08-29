using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config.Scheduling;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckQueue;

[ApiController]
[Route("api/get-health-check-queue")]
public class GetHealthCheckQueueController(
    DavDatabaseClient dbClient,
    HealthWorkSchedulePolicy healthWorkSchedule
) : BaseApiController
{
    private async Task<GetHealthCheckQueueResponse> GetHealthCheckQueue(GetHealthCheckQueueRequest request)
    {
        // Stream the ordered queue and filter non-media files so the Health UI focuses on
        // playable media. Urgent and pending repairs are always included.
        var davItems = new List<Database.Models.DavItem>();
        await foreach (var item in HealthCheckService.GetHealthCheckQueueItems(dbClient)
            .AsAsyncEnumerable()
            .ConfigureAwait(false))
        {
            if (davItems.Count >= request.PageSize) break;
            if (item.NextHealthCheck == DateTimeOffset.UnixEpoch ||
                item.HealthRepairPending ||
                FilenameUtil.IsHealthCheckCandidate(item.Name))
            {
                davItems.Add(item);
            }
        }

        // Match HealthCheckService.ExecuteAsync: only media/archive candidates are ever
        // processed, so non-media files (nfo/srt/jpg/…) must not inflate this count or
        // the Health UI "initial scan pending" banner never clears. Pending repairs are
        // counted separately on the schedule snapshot, while operator-forced rechecks
        // count as pending alongside never-checked files.
        var uncheckedCount = 0;
        await foreach (var name in HealthCheckService.GetHealthCheckQueueItemsQuery(dbClient)
            .Where(x => !x.HealthRepairPending &&
                (x.NextHealthCheck == null ||
                 x.NextHealthCheck == HealthCheckService.ForcedRecheckSentinel))
            .Select(x => x.Name)
            .AsAsyncEnumerable()
            .ConfigureAwait(false))
        {
            if (FilenameUtil.IsHealthCheckCandidate(name)) uncheckedCount++;
        }

        var pendingRepairCount = await dbClient.Ctx.Items
            .CountAsync(x => x.HealthRepairPending, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        var admission = healthWorkSchedule.Evaluate(DateTimeOffset.UtcNow);

        return new GetHealthCheckQueueResponse()
        {
            UncheckedCount = uncheckedCount,
            Schedule = new GetHealthCheckQueueResponse.HealthCheckScheduleStatus
            {
                TimeZoneId = admission.TimeZoneId,
                ChecksOpen = admission.ChecksOpen,
                RepairsOpen = admission.RepairsOpen,
                NextChecksChange = admission.NextChecksChange,
                NextRepairsChange = admission.NextRepairsChange,
                PendingRepairCount = pendingRepairCount,
                ManualRunActive = admission.ManualRunActive,
            },
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
