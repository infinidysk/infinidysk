using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.ProwlarrSync;

/// <summary>
/// GET reads the last pull-sync status without network access. POST runs a sync now.
/// </summary>
[ApiController]
[Route("api/prowlarr-sync")]
public class ProwlarrSyncController(ProwlarrSyncService syncService) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var snapshot = HttpMethods.IsPost(HttpContext.Request.Method)
            ? await syncService.SyncNowAsync(HttpContext.RequestAborted).ConfigureAwait(false)
            : syncService.GetSnapshot();

        return Ok(ProwlarrSyncResponse.FromSnapshot(snapshot));
    }
}
