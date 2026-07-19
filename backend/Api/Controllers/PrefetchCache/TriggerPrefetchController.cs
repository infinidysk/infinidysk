using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.PrefetchCache;

/// <summary>
/// Manually starts a prefetch of the given DavItem into the local cache. Used to
/// validate the prefetch/eviction pipeline end-to-end without depending on a Jellyfin
/// webhook (Phase 1), and doubles as the backend for a future manual "prefetch now"
/// action (e.g. from the Explore file browser).
/// </summary>
[ApiController]
[Route("api/prefetch-cache/trigger")]
public class TriggerPrefetchController(PrefetchCacheService prefetchCacheService) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TriggerPrefetchRequest(HttpContext);
        var result = await prefetchCacheService
            .TriggerPrefetchAsync(request.DavItemId, request.CancellationToken)
            .ConfigureAwait(false);

        return Ok(new TriggerPrefetchResponse { Result = result.ToString() });
    }
}
