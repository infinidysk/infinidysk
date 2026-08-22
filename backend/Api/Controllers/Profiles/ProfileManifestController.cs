using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Profiles;

[ApiController]
[Route("adapters/addon/{token}/manifest.json")]
public class ProfileManifestController(SearchProfileService searchService) : ControllerBase
{
    [HttpOptions]
    public IActionResult Preflight()
    {
        SetCors(Response);
        return NoContent();
    }

    [HttpGet]
    public IActionResult Get(string token)
    {
        SetCors(Response);
        var profile = searchService.GetProfile(token);
        if (profile is null) return NotFound();
        if (!searchService.IsAdapterEnabled(token, "addon")) return NotFound();

        return new JsonResult(ProfileAddonFactory.CreateManifest(profile, token));
    }

    internal static void SetCors(HttpResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "*";
        response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    }
}
