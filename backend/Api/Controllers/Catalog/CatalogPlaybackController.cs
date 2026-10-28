using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Catalog;
using NzbWebDAV.Services.Catalog;

namespace NzbWebDAV.Api.Controllers.Catalog;

/// <summary>
/// Creates a playback session for one movie or episode. Only provider coordinates are
/// accepted from the browser: external ids, NZB URLs, and profile tokens are resolved
/// server-side, so a client cannot point the player at an arbitrary release.
/// </summary>
[ApiController]
[Route("api/catalog/playback")]
[ProducesResponseType(typeof(CatalogPlaybackResponse), 200)]
public sealed class CatalogPlaybackController(
    CatalogPlaybackService playbackService,
    ConfigManager configManager) : PostOnlyApiController
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task<IActionResult> HandleRequest()
    {
        Response.Headers.CacheControl = "no-store";
        var request = await ReadRequestAsync(HttpContext).ConfigureAwait(false);

        var baseUrl = configManager.GetBaseUrl();
        var playback = await playbackService.CreateAsync(
                request,
                HttpContext.GetPublicBaseUrl(baseUrl),
                HttpContext.GetPublicPathPrefix(baseUrl),
                HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return Ok(new CatalogPlaybackResponse
        {
            PlaybackId = playback.PlaybackId,
            DirectUrl = playback.DirectUrl,
            HlsUrl = playback.HlsUrl,
            ReleaseTitle = playback.ReleaseTitle,
        });
    }

    internal static async Task<CreatePlaybackRequest> ReadRequestAsync(HttpContext context)
    {
        CreatePlaybackRequest? request;
        try
        {
            request = await JsonSerializer
                .DeserializeAsync<CreatePlaybackRequest>(context.Request.Body, RequestJsonOptions, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw CatalogException.Validation("The playback request could not be read.");
        }

        if (request is null)
            throw CatalogException.Validation("The playback request could not be read.");

        var type = request.Type?.Trim().ToLowerInvariant();
        if (type is not ("movie" or "series"))
            throw CatalogException.Validation("Title type must be 'movie' or 'series'.");
        if (request.TmdbId <= 0)
            throw CatalogException.Validation("TMDB id must be a positive integer.");

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
