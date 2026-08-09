using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Profiles.Adapters;

[ApiController]
[Route("api/search/{token}/lookup")]
public class JsonSearchController(
    SearchProfileService searchService,
    ConfigManager configManager
) : ControllerBase
{
    [HttpOptions]
    public IActionResult Preflight()
    {
        SetCors(Response);
        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        string token,
        [FromQuery] string? type,
        [FromQuery] string? id,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        [FromQuery] string? q)
    {
        SetCors(Response);

        var profile = searchService.GetProfile(token);
        if (profile is null) return NotFound();
        if (!searchService.IsAdapterEnabled(token, "json")) return NotFound();

        var ct = HttpContext.RequestAborted;

        SearchProfileService.SearchResult? result;
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = "type and id are required (type=movie|series, id=tt1234567)." });
        }

        if (type.Equals("series", StringComparison.OrdinalIgnoreCase)
            && season.HasValue && episode.HasValue
            && !id.Contains(':', StringComparison.Ordinal))
        {
            id = $"{id}:{season.Value}:{episode.Value}";
        }

        result = await searchService.SearchByImdbAsync(token, type, id, ct, q).ConfigureAwait(false);

        if (result is null) return NotFound();

        var baseUrl = HttpContext.GetPublicBaseUrl(configManager.GetBaseUrl());
        var items = result.Candidates
            .Select((c, i) => new
            {
                title = c.Title,
                indexer = c.IndexerName,
                sourceIndexer = c.SourceIndexerName,
                sizeBytes = c.Size,
                postedAt = c.Posted,
                usenetDate = c.UsenetDate,
                grabs = c.Grabs,
                passwordFlag = c.Password,
                language = c.Language,
                subs = c.Subs,
                infoHash = c.InfoHash,
                playUrl = $"{baseUrl}/api/search/{token}/play/{result.PlayTokens[i]}.mkv",
            })
            .ToList();

        return new JsonResult(new
        {
            profile = profile.Name,
            type = result.Type,
            id = result.Id,
            count = items.Count,
            results = items,
        });
    }

    internal static void SetCors(HttpResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "*";
        response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    }
}
