using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.RcloneMounts;

[ApiController]
[Route("api/rclone-mounts/logs")]
public class GetRcloneMountLogsController(RcloneDaemonService daemonService) : GetOnlyApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        return Task.FromResult<IActionResult>(Ok(new RcloneMountLogsResponse
        {
            Status = true,
            Lines = [.. daemonService.GetRecentLogLines()],
        }));
    }
}
