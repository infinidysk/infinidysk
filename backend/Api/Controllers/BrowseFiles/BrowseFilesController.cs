using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Config.Scheduling;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.BrowseFiles;

[ApiController]
[Route("api/browse-files")]
[ProducesResponseType(typeof(BrowseFilesResponse), 200)]
public sealed class BrowseFilesController(DavDatabaseClient dbClient, ConfigManager configManager,
    HealthCheckService healthCheckService, HealthWorkSchedulePolicy healthWorkSchedule,
    QueueManager queueManager, FilesLibraryIndex libraryIndex) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        Response.Headers.CacheControl = "private, no-store";
        var request = new BrowseFilesRequest(HttpContext);
        var now = DateTimeOffset.UtcNow;
        var active = healthCheckService.GetActiveHealthCheckProgress();
        var queue = queueManager.GetInProgressQueueItems();
        var library = libraryIndex.GetSnapshot();
        try
        {
            return Ok(await BrowseFilesQuery.ReadAsync(dbClient, configManager, request, library, active, queue,
                healthWorkSchedule, now, HttpContext.RequestAborted).ConfigureAwait(false));
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException exception) when (exception.StatusCode == 404)
        {
            return NotFound(new BaseApiResponse { Status = false, Error = "Directory not found." });
        }
    }
}