using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.Controllers.RemoveMissingPayloads;

[ApiController]
[Route("api/remove-missing-payloads/dry-run")]
public sealed class RemoveMissingPayloadsDryRunController(
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
            isDryRun: true);
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
                PreviewToken = task.IssuedPreviewToken,
            })
            : BadRequest(new RemoveMissingPayloadsTaskResponse
            {
                Status = false,
                Message = null,
                Error = task.TerminalMessage,
            });
    }
}
