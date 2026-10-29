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

        if (!await healthCheckService.StopHealthCheckAsync(davItemId).ConfigureAwait(false))
            return NotFound(new BaseApiResponse { Status = false, Error = "This health check is no longer running." });

        return Ok(new BaseApiResponse { Status = true });
    }
}
