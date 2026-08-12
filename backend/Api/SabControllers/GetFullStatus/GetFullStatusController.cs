using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.SabControllers.GetFullStatus;

public class GetFullStatusController(
    HttpContext httpContext,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override Task<IActionResult> Handle()
    {
        // mimic sabnzbd fullstatus
        var speedLimitKbps = Config.GetSabSpeedLimitKbps();
        var status = new GetFullStatusResponse()
        {
            Status = new SabStatusObject
            {
                CompleteDir = SabPathResolver.GetCompletedDir(Config),
                Paused = Config.IsSabQueuePaused(),
                SpeedLimit = speedLimitKbps.ToString(),
                SpeedLimitAbs = speedLimitKbps.ToString(),
            }
        };

        return Task.FromResult<IActionResult>(Ok(status));
    }
}
