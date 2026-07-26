using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.GetProviderUsageStats;

public class GetProviderUsageStatsRequest
{
    public CancellationToken CancellationToken { get; init; }

    public GetProviderUsageStatsRequest(HttpContext context)
    {
        CancellationToken = context.RequestAborted;
    }
}
