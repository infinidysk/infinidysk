using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueRequest
{
    public int Start { get; init; }
    public int Limit { get; init; } = int.MaxValue;
    public string? Category { get; init; }
    public CancellationToken CancellationToken { get; init; }


    public GetQueueRequest(HttpContext context, ConfigManager configManager)
    {
        var startParam = context.GetRequestParam("start");
        var limitParam = context.GetRequestParam("limit");
        Category = SabCategoryResolver.GetCategory(context, configManager);
        CancellationToken = context.RequestAborted;

        if (startParam is not null)
        {
            var isValidStartParam = int.TryParse(startParam, out int start);
            if (!isValidStartParam) throw new BadHttpRequestException("Invalid start parameter");
            Start = Math.Max(0, start);
        }

        if (limitParam is not null)
        {
            var isValidLimit = int.TryParse(limitParam, out int limit);
            if (!isValidLimit) throw new BadHttpRequestException("Invalid limit parameter");
            Limit = limit > 0 ? limit : int.MaxValue;
        }
    }
}
