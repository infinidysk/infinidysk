using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Catalog;

namespace NzbWebDAV.Api.Controllers.Catalog;

[ApiController]
[Route("api/catalog/home")]
[ProducesResponseType(typeof(CatalogHomeResponse), 200)]
public sealed class CatalogHomeController(CatalogService catalogService) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        Response.Headers.CacheControl = "no-store";
        var home = await catalogService.GetHomeAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(CatalogContractMapper.ToResponse(home));
    }
}

[ApiController]
[Route("api/catalog/search")]
[ProducesResponseType(typeof(CatalogSearchResponse), 200)]
public sealed class CatalogSearchController(CatalogService catalogService) : GetOnlyApiController
{
    internal const int MaxQueryLength = 100;
    internal const int MaxPage = 20;

    protected override async Task<IActionResult> HandleRequest()
    {
        Response.Headers.CacheControl = "no-store";
        var query = HttpContext.Request.Query["q"].ToString().Trim();
        if (query.Length is < 1 or > MaxQueryLength)
            throw CatalogException.Validation($"Search query must be between 1 and {MaxQueryLength} characters.");

        var page = ReadPage(HttpContext.Request.Query["page"].ToString());
        var results = await catalogService
            .SearchAsync(query, page, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(CatalogContractMapper.ToResponse(results));
    }

    private static int ReadPage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 1;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
            || page < 1
            || page > MaxPage)
        {
            throw CatalogException.Validation($"Search page must be between 1 and {MaxPage}.");
        }

        return page;
    }
}

[ApiController]
[Route("api/catalog/titles/{type}/{tmdbId:int}")]
[ProducesResponseType(typeof(CatalogTitleResponse), 200)]
public sealed class CatalogTitleController(CatalogService catalogService) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        Response.Headers.CacheControl = "no-store";
        var type = ReadType();
        var tmdbId = ReadTmdbId();
        var season = ReadSeason();

        var details = await catalogService
            .GetTitleAsync(type, tmdbId, season, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        if (details is null)
            return NotFound(new BaseApiResponse { Status = false, Error = "Title not found." });

        return Ok(CatalogContractMapper.ToResponse(details));
    }

    private string ReadType()
    {
        var raw = HttpContext.Request.RouteValues["type"]?.ToString();
        if (string.Equals(raw, "movie", StringComparison.OrdinalIgnoreCase)) return "movie";
        if (string.Equals(raw, "series", StringComparison.OrdinalIgnoreCase)) return "series";
        throw CatalogException.Validation("Title type must be 'movie' or 'series'.");
    }

    private int ReadTmdbId()
    {
        var raw = HttpContext.Request.RouteValues["tmdbId"]?.ToString();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId) || tmdbId <= 0)
            throw CatalogException.Validation("TMDB id must be a positive integer.");
        return tmdbId;
    }

    private int? ReadSeason()
    {
        var raw = HttpContext.Request.Query["season"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var season) || season < 0)
            throw CatalogException.Validation("Season must be zero or greater.");
        return season;
    }
}
