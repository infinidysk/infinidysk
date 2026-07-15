using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.Controllers.RemoveSampleFiles;

[ApiController]
[Route("api/remove-sample-files/dry-run")]
public class RemoveSampleFilesDryRunController(
    ConfigManager configManager,
    WebsocketManager websocketManager
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var triggerArrSearch = HttpContext.GetQueryParam("triggerArrSearch")?.ToLower() != "false";
        var task = new RemoveSampleFilesTask(configManager, websocketManager, isDryRun: true, triggerArrSearch);
        var executed = await task.Execute().ConfigureAwait(false);
        return Ok(executed);
    }
}
