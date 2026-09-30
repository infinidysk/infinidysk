using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.Library;

namespace NzbWebDAV.Api.Controllers.GetLibraryCatalog;

public class GetLibraryCatalogRequest
{
    private static readonly HashSet<string> AllowedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "all", "internal", "external", "broken" };
    private static readonly HashSet<string> AllowedSorts =
        new(StringComparer.OrdinalIgnoreCase) { "name", "size", "mappings" };

    public LibraryCatalogQuery Query { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public GetLibraryCatalogRequest(HttpContext context)
    {
        CancellationToken = context.RequestAborted;
        var errors = new ValidationErrors();

        var page = 1;
        var pageSize = 25;
        if (errors.TryParseInt("page", context.GetQueryParam("page"), "Invalid page parameter", out var parsedPage))
            page = Math.Max(1, parsedPage);
        if (errors.TryParseInt("pageSize", context.GetQueryParam("pageSize"), "Invalid pageSize parameter", out var parsedSize))
            pageSize = parsedSize;

        var type = context.GetQueryParam("type") ?? "all";
        if (!AllowedTypes.Contains(type)) errors.Add("type", "Invalid type parameter.");
        var sort = context.GetQueryParam("sort") ?? "name";
        if (!AllowedSorts.Contains(sort)) errors.Add("sort", "Invalid sort parameter.");
        var dir = (context.GetQueryParam("dir") ?? "asc").ToLowerInvariant();
        if (dir is not ("asc" or "desc")) errors.Add("dir", "Invalid dir parameter.");

        var search = context.GetQueryParam("q");
        if (search is { Length: > 200 }) errors.Add("q", "Search query is too long.");
        errors.ThrowIfAny();

        Query = new LibraryCatalogQuery
        {
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            TypeFilter = type.ToLowerInvariant(),
            Sort = sort.ToLowerInvariant(),
            Direction = dir,
            Page = page,
            PageSize = Math.Clamp(pageSize, 1, 100),
        };
    }
}
