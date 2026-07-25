using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.SabControllers.Pause;

/// <summary>
/// SAB-compatible <c>mode=pause</c> (and <c>mode=queue&amp;name=pause</c>).
/// Stops the queue coordinator from starting new downloads; items already in
/// progress finish naturally and WebDAV keeps serving mounted content.
/// </summary>
public class PauseController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<PauseResponse> Pause(CancellationToken ct)
    {
        await ConfigPersistenceUtil.SetValueAsync(
            dbClient, configManager, ConfigKeys.QueuePaused, "true", ct).ConfigureAwait(false);
        return new PauseResponse { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        return Ok(await Pause(httpContext.RequestAborted).ConfigureAwait(false));
    }
}
