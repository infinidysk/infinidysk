using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.RequeueActionNeededHealthChecks;

[ApiController]
[Route("api/requeue-action-needed-health-checks")]
public class RequeueActionNeededHealthChecksController(
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(HttpContext.Request.Method))
        {
            return StatusCode(
                StatusCodes.Status405MethodNotAllowed,
                new BaseApiResponse { Status = false, Error = "POST required" });
        }

        if (!configManager.IsRepairJobEnabled())
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                new BaseApiResponse
                {
                    Status = false,
                    Error = configManager.GetRepairDisabledReason() ?? "Background repairs are disabled.",
                });
        }

        Guid? itemId = null;
        if (HttpContext.Request.Query.TryGetValue("davItemId", out var itemIdParam))
        {
            if (!Guid.TryParse(itemIdParam, out var parsedId))
                return BadRequest(new BaseApiResponse { Status = false, Error = "Invalid davItemId parameter." });
            itemId = parsedId;
        }

        var requeuedCount = itemId is Guid selectedId
            ? await HealthCheckQueueMutations.RequeueActionNeededIdsAsync(
                dbClient.Ctx, [selectedId], HttpContext.RequestAborted).ConfigureAwait(false)
            : await HealthCheckQueueMutations.RequeueLatestActionNeededAsync(
                dbClient.Ctx, HttpContext.RequestAborted).ConfigureAwait(false);

        return Ok(new RequeueActionNeededHealthChecksResponse
        {
            Status = true,
            RequeuedCount = requeuedCount,
        });
    }
}
