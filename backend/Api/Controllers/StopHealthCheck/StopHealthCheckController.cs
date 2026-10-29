using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.StopHealthCheck;

[ApiController]
[Route("api/stop-health-check")]
public class StopHealthCheckController(HealthCheckService healthCheckService) : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!Guid.TryParse(HttpContext.Request.Query["davItemId"], out var davItemId))
            return BadRequest(new BaseApiResponse { Status = false, Error = "Invalid davItemId parameter." });

        return await healthCheckService.StopHealthCheckAsync(davItemId, HttpContext.RequestAborted).ConfigureAwait(false) switch
        {
            HealthCheckService.StopHealthCheckOutcome.Stopped => Ok(new BaseApiResponse { Status = true }),
            HealthCheckService.StopHealthCheckOutcome.Stopping => Accepted(new BaseApiResponse { Status = true }),
            _ => NotFound(new BaseApiResponse { Status = false, Error = "This health check is no longer running." }),
        };
    }
}
