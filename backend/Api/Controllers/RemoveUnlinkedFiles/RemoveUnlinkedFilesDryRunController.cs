using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.Controllers.RemoveUnlinkedFiles;

[ApiController]
[Route("api/remove-unlinked-files/dry-run")]
public class RemoveUnlinkedFilesDryRunController(
    ConfigManager configManager,
    WebsocketManager websocketManager
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var task = new RemoveUnlinkedFilesTask(configManager, websocketManager, isDryRun: true);
        var executed = await task.Execute().ConfigureAwait(false);
        if (!executed)
            return Conflict(new RemoveUnlinkedFilesTaskResponse
            {
                Status = false,
                Error = "Remove Orphaned Files task is already running.",
            });
        return Ok(new RemoveUnlinkedFilesTaskResponse
        {
            PreviewToken = task.IssuedPreviewToken,
        });
    }
}
