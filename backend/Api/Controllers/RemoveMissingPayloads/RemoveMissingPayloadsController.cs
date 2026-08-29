using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.Controllers.RemoveMissingPayloads;

[ApiController]
[Route("api/remove-missing-payloads")]
public sealed class RemoveMissingPayloadsController(
    ConfigManager configManager,
    WebsocketManager websocketManager,
    ArrReplacementSearchBudget replacementSearchBudget
) : PostOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var task = new RemoveMissingPayloadsTask(
            configManager,
            websocketManager,
            replacementSearchBudget,
            isDryRun: false,
            previewToken: Request.Headers["X-InfiniDysk-Cleanup-Preview"].FirstOrDefault());
        var executed = await task.Execute().ConfigureAwait(false);
        if (!executed)
            return Conflict(new RemoveMissingPayloadsTaskResponse
            {
                Status = false,
                Message = null,
                Error = "Another maintenance task is already running.",
            });
        return task.Succeeded
            ? Ok(new RemoveMissingPayloadsTaskResponse
            {
                Status = true,
                Message = task.TerminalMessage,
            })
            : BadRequest(new RemoveMissingPayloadsTaskResponse
            {
                Status = false,
                Message = null,
                Error = task.TerminalMessage,
            });
    }
}
