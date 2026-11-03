using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Catalog;
using NzbWebDAV.Services.Catalog;

namespace NzbWebDAV.Api.Controllers.Catalog;

/// <summary>
/// Watch history reads. The Continue Watching row is embedded in the home response; this
/// endpoint exists for the title page's Resume button and for player bootstrap.
/// </summary>
[ApiController]
[Route("api/catalog/history")]
[ProducesResponseType(typeof(CatalogHistoryResponse), 200)]
public sealed class CatalogHistoryController(CatalogWatchHistoryService historyService) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        Response.Headers.CacheControl = "no-store";
        var entries = await historyService
            .ListAsync(HttpContext.GetViewerProfileId(), ReadLimit(), HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new CatalogHistoryResponse
        {
            Items = entries.Select(CatalogContractMapper.ToHistoryItem).ToList(),
        });
    }

    private int ReadLimit()
    {
        var raw = HttpContext.Request.Query["limit"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return CatalogWatchHistoryService.DefaultLimit;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit <= 0)
            throw CatalogException.Validation("Limit must be a positive integer.");
        return Math.Min(limit, CatalogWatchHistoryService.MaxLimit);
    }
}

/// <summary>
/// Records playback position for the Continue Watching row. Only coordinates and the
/// position travel from the browser: titles and artwork are resolved from provider
/// metadata server-side, so the client cannot write arbitrary text into history.
/// </summary>
[ApiController]
[Route("api/catalog/history/progress")]
[ProducesResponseType(typeof(CatalogHistoryProgressResponse), 200)]
public sealed class CatalogHistoryProgressController(
    CatalogWatchHistoryService historyService,
    CatalogService catalogService) : PostOnlyApiController
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task<IActionResult> HandleRequest()
    {
        Response.Headers.CacheControl = "no-store";
        var request = await ReadRequestAsync(HttpContext).ConfigureAwait(false);
        var ct = HttpContext.RequestAborted;

        var display = await ResolveDisplayAsync(request, catalogService, ct).ConfigureAwait(false);
        await historyService.ReportProgressAsync(
                new ReportProgressRequest(
                    request.Type,
                    request.TmdbId,
                    request.Season,
                    request.Episode,
                    request.Position,
                    request.Duration,
                    request.Finished,
                    display?.Title,
                    display?.Subtitle,
                    display?.PosterUrl,
                    display?.BackdropUrl,
                    ProfileId: HttpContext.GetViewerProfileId()),
                ct)
            .ConfigureAwait(false);

        return Ok(new CatalogHistoryProgressResponse());
    }

    /// <summary>
    /// Provider metadata can fail while the local database is healthy; a missing display
    /// record degrades the row's artwork instead of rejecting the position write.
    /// </summary>
    private static async Task<CatalogHistoryDisplay?> ResolveDisplayAsync(
        CreateHistoryProgressRequest request,
        CatalogService catalogService,
        CancellationToken ct)
    {
        try
        {
            return await catalogService
                .GetHistoryDisplayAsync(request.Type, request.TmdbId, request.Season, request.Episode, ct)
                .ConfigureAwait(false);
        }
        catch (CatalogException)
        {
            return null;
        }
    }

    internal static async Task<CreateHistoryProgressRequest> ReadRequestAsync(HttpContext context)
    {
        CreateHistoryProgressRequest? request;
        try
        {
            request = await JsonSerializer
                .DeserializeAsync<CreateHistoryProgressRequest>(context.Request.Body, RequestJsonOptions, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw CatalogException.Validation("The progress request could not be read.");
        }

        if (request is null)
            throw CatalogException.Validation("The progress request could not be read.");

        var type = request.Type?.Trim().ToLowerInvariant();
        if (type is not ("movie" or "series"))
            throw CatalogException.Validation("Title type must be 'movie' or 'series'.");
        if (request.TmdbId <= 0)
            throw CatalogException.Validation("TMDB id must be a positive integer.");
        if (request.Position is < 0)
            throw CatalogException.Validation("Position must be zero or greater.");
        if (request.Duration is < 0)
            throw CatalogException.Validation("Duration must be zero or greater.");

        if (type == "movie")
        {
            if (request.Season is not null || request.Episode is not null)
                throw CatalogException.Validation("Movies cannot specify a season or episode.");
            return request with { Type = type, Season = null, Episode = null };
        }

        if (request.Season is not >= 0)
            throw CatalogException.Validation("Season must be zero or greater.");
        if (request.Episode is not >= 1)
            throw CatalogException.Validation("Episode must be one or greater.");

        return request with { Type = type };
    }
}

/// <summary>Progress write body. Display fields are resolved server-side, never sent here.</summary>
public sealed record CreateHistoryProgressRequest(
    string Type,
    int TmdbId,
    int? Season,
    int? Episode,
    double? Position,
    double? Duration,
    bool Finished);

/// <summary>
/// Removes one history row — the "Remove from Continue Watching" action, and the only
/// way to drop a title that was reported by mistake before the retention window ends.
/// </summary>
[ApiController]
[Route("api/catalog/history/{id:int}")]
[ProducesResponseType(typeof(CatalogHistoryProgressResponse), 200)]
public sealed class CatalogHistoryItemController(CatalogWatchHistoryService historyService)
    : DeleteOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        Response.Headers.CacheControl = "no-store";
        var raw = HttpContext.Request.RouteValues["id"]?.ToString();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw CatalogException.Validation("History id must be a positive integer.");

        var removed = await historyService
            .RemoveAsync(HttpContext.GetViewerProfileId(), id, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        if (!removed)
            return NotFound(new BaseApiResponse { Status = false, Error = "History entry not found." });

        return Ok(new CatalogHistoryProgressResponse());
    }
}
