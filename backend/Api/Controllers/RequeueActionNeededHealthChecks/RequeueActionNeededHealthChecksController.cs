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

        var requeuedCount = await HealthCheckQueueMutations
            .RequeueLatestActionNeededAsync(dbClient.Ctx, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return Ok(new RequeueActionNeededHealthChecksResponse
        {
            Status = true,
            RequeuedCount = requeuedCount,
        });
    }
}
