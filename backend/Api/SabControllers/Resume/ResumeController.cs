using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.SabControllers.Resume;

/// <summary>
/// SAB-compatible <c>mode=resume</c> (and <c>mode=queue&amp;name=resume</c>).
/// Clears the queue pause flag and wakes the queue coordinator so it starts
/// claiming work again without waiting for its idle-poll interval.
/// </summary>
public class ResumeController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<ResumeResponse> Resume(CancellationToken ct)
    {
        await ConfigPersistenceUtil.SetValueAsync(
            dbClient, configManager, ConfigKeys.QueuePaused, "false", ct).ConfigureAwait(false);
        queueManager.AwakenQueue();
        return new ResumeResponse { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        return Ok(await Resume(httpContext.RequestAborted).ConfigureAwait(false));
    }
}
