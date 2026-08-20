using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.SearchIndexers;

public class SearchIndexersRequest
{
    public string Query { get; init; }
    public int Limit { get; init; }

    public SearchIndexersRequest(HttpContext context)
    {
        var errors = new ValidationErrors();
        Query = context.GetRequestParam("q") ?? "";
        if (string.IsNullOrEmpty(Query))
            errors.Add("q", "Query `q` is required");
        errors.ThrowIfAny();

        Limit = int.TryParse(context.GetRequestParam("limit"), out var n) && n is > 0 and <= 500
            ? n
            : 100;
    }
}
