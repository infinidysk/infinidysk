using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.SabControllers.GetStatus;

public class GetStatusController(
    HttpContext httpContext,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override Task<IActionResult> Handle()
    {
        var speedLimitKbps = Config.GetSabSpeedLimitKbps();
        var response = new GetStatusResponse
        {
            Status = new SabStatusObject
            {
                CompleteDir = SabPathResolver.GetCompletedDir(Config),
                Paused = Config.IsSabQueuePaused(),
                SpeedLimit = speedLimitKbps.ToString(),
                SpeedLimitAbs = speedLimitKbps.ToString(),
            },
        };
        return Task.FromResult<IActionResult>(Ok(response));
    }
}
