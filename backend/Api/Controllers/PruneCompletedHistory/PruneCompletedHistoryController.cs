using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.Controllers.PruneCompletedHistory;

[ApiController]
[Route("api/prune-completed-history")]
public class PruneCompletedHistoryController(WebsocketManager websocketManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var category = HttpContext.Request.Query["category"].FirstOrDefault();
        var olderThanDays = ParseOlderThanDays(HttpContext.Request.Query["older-than-days"].FirstOrDefault());
        var task = new PruneCompletedHistoryTask(websocketManager, isDryRun: false, category, olderThanDays);
        var executed = await task.Execute().ConfigureAwait(false);
        if (!executed) return Conflict(new { error = "A maintenance task is already running." });
        return Ok(executed);
    }

    internal static int? ParseOlderThanDays(string? value) =>
        int.TryParse(value, out var days) && days > 0 ? days : null;
}
