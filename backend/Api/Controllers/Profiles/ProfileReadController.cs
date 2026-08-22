using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Profiles;

[ApiController]
[Route("adapters/addon/{token}/stream/{type}/{id}.json")]
public class ProfileReadController(
    SearchProfileService searchService,
    ConfigManager configManager,
    ProfileStreamStateService streamState,
    PreflightCache preflightCache
) : ControllerBase
{
    [HttpOptions]
    public IActionResult Preflight()
    {
        ProfileManifestController.SetCors(Response);
        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> Get(string token, string type, string id)
    {
        ProfileManifestController.SetCors(Response);

        var profile = searchService.GetProfile(token);
        if (profile is null) return NotFound();
        if (!searchService.IsAdapterEnabled(token, "addon")) return NotFound();

        var ct = HttpContext.RequestAborted;
        var result = await searchService.SearchByImdbAsync(token, type, id, ct).ConfigureAwait(false);
        if (result is null) return NotFound();

        var readyNames = await streamState.GetReadyNzbFileNamesAsync(result.Candidates, ct).ConfigureAwait(false);
        var baseUrl = HttpContext.GetPublicBaseUrl(configManager.GetBaseUrl());
        return new JsonResult(ProfileAddonFactory.CreateStreamResponse(
            result,
            baseUrl,
            readyNames,
            nzbUrl => preflightCache.Get(nzbUrl)?.Verdict == PlaybackFastVerifier.Verdict.Available));
    }
}
