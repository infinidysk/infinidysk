using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Database;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.Library;
using NzbWebDAV.Services.Plex;
using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Api.Controllers.GetLibraryBrowse;

[ApiController]
[Route("api/get-library-browse")]
[ProducesResponseType(typeof(LibraryBrowseResult), StatusCodes.Status200OK)]
public sealed class GetLibraryBrowseController(DavDatabaseClient dbClient,
    PlexLibraryMetadataService plexMetadata, NativeCacheService nativeCache,
    ConfigManager config) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!config.IsMediaLibraryEnabled())
            return NotFound(new { status = false, error = "Media Library is disabled." });
        var request = new GetLibraryBrowseRequest(HttpContext);
        var scanner = HttpContext.RequestServices.GetService<LibraryCatalogScanner>();
        var catalog = new LibraryCatalogService(dbClient.Ctx, config.IsMediaLibraryVideoOnly());
        var browse = new LibraryBrowseService(catalog, plexMetadata, nativeCache);
        var result = await browse.QueryAsync(request.Query, scanner, request.CancellationToken)
            .ConfigureAwait(false);
        return Ok(result);
    }
}

public sealed class GetLibraryBrowseRequest
{
    private static readonly HashSet<string> AllowedCategories =
        new(StringComparer.OrdinalIgnoreCase) { "all", "shows", "movies", "unmatched" };
    private static readonly HashSet<string> AllowedViews =
        new(StringComparer.OrdinalIgnoreCase) { "groups", "files" };
    private static readonly HashSet<string> AllowedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "all", "internal", "external", "broken" };
    private static readonly HashSet<string> AllowedQualities =
        new(StringComparer.OrdinalIgnoreCase) { "all", "4k", "1080p", "720p", "sd", "unknown" };
    private static readonly HashSet<string> AllowedCacheFilters =
        new(StringComparer.OrdinalIgnoreCase) { "all", "any", "complete", "empty", "unavailable" };
    private static readonly HashSet<string> AllowedMatchFilters =
        new(StringComparer.OrdinalIgnoreCase) { "all", "matched", "unmatched" };

    public LibraryBrowseQuery Query { get; }
    public CancellationToken CancellationToken { get; }

    public GetLibraryBrowseRequest(HttpContext context)
    {
        CancellationToken = context.RequestAborted;
        var errors = new ValidationErrors();
        var category = context.GetQueryParam("category") ?? "shows";
        var view = context.GetQueryParam("view") ?? "groups";
        var type = context.GetQueryParam("type") ?? "all";
        var quality = context.GetQueryParam("quality") ?? "all";
        var cache = context.GetQueryParam("cache") ?? "all";
        var match = context.GetQueryParam("match") ?? "all";
        if (!AllowedCategories.Contains(category)) errors.Add("category", "Invalid category parameter.");
        if (!AllowedViews.Contains(view)) errors.Add("view", "Invalid view parameter.");
        if (!AllowedTypes.Contains(type)) errors.Add("type", "Invalid type parameter.");
        if (!AllowedQualities.Contains(quality)) errors.Add("quality", "Invalid quality parameter.");
        if (!AllowedCacheFilters.Contains(cache)) errors.Add("cache", "Invalid cache parameter.");
        if (!AllowedMatchFilters.Contains(match)) errors.Add("match", "Invalid match parameter.");
        var search = context.GetQueryParam("q");
        if (search is { Length: > 200 }) errors.Add("q", "Search query is too long.");
        var group = context.GetQueryParam("group");
        if (group is { Length: > 500 }) errors.Add("group", "Group key is too long.");
        var page = 1;
        var groupPage = 1;
        int? season = null;
        if (context.GetQueryParam("season") is { } seasonText)
        {
            if (int.TryParse(seasonText, out var parsedSeason) && parsedSeason is >= 0 and <= 1000)
                season = parsedSeason;
            else errors.Add("season", "Season must be between 0 and 1000.");
        }
        if (errors.TryParseInt("page", context.GetQueryParam("page"), "Invalid page parameter", out var parsedPage))
            page = Math.Max(1, parsedPage);
        if (errors.TryParseInt("groupPage", context.GetQueryParam("groupPage"), "Invalid groupPage parameter", out var parsedGroupPage))
            groupPage = Math.Max(1, parsedGroupPage);
        errors.ThrowIfAny();

        Query = new LibraryBrowseQuery
        {
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            Category = category.ToLowerInvariant(),
            View = view.ToLowerInvariant(),
            TypeFilter = type.ToLowerInvariant(),
            Quality = quality.ToLowerInvariant(),
            Cache = cache.ToLowerInvariant(),
            MatchFilter = match.ToLowerInvariant(),
            SeasonFilter = season,
            Page = page,
            GroupKey = group,
            GroupPage = groupPage,
        };
    }
}
