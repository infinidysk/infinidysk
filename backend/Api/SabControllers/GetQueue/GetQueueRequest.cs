using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueRequest
{
    public int Start { get; init; }
    public int Limit { get; init; } = int.MaxValue;
    public string? Category { get; init; }
    public string? Search { get; init; }
    public string? Status { get; init; }
    public string? Sort { get; init; }
    public string? Direction { get; init; }
    public CancellationToken CancellationToken { get; init; }


    public GetQueueRequest(HttpContext context, ConfigManager configManager)
    {
        var errors = new ValidationErrors();
        var startParam = context.GetRequestParam("start");
        var limitParam = context.GetRequestParam("limit");
        Category = SabCategoryResolver.GetCategory(context, configManager);
        Search = SabListQuery.NormalizeSearch(context.GetRequestParam("search"));
        Status = NormalizeStatus(context.GetRequestParam("status"));
        Sort = NormalizeSort(context.GetRequestParam("sort"));
        Direction = SabListQuery.NormalizeDirection(context.GetRequestParam("dir"));
        CancellationToken = context.RequestAborted;

        if (startParam is not null)
        {
            if (errors.TryParseInt("start", startParam, "Invalid start parameter", out var start))
                Start = Math.Max(0, start);
        }

        if (limitParam is not null)
        {
            if (errors.TryParseInt("limit", limitParam, "Invalid limit parameter", out var limit))
                Limit = limit > 0 ? limit : int.MaxValue;
        }

        errors.ThrowIfAny();
    }

    private static string? NormalizeStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "downloading" => "Downloading",
        "queued" => "Queued",
        "paused" => "Paused",
        _ => "Unsupported",
    };

    private static string? NormalizeSort(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "name" or "category" or "status" or "size" ? normalized : null;
    }
}
