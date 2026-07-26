using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.GetProviderUsageStats;

public class GetProviderUsageStatsResponse : BaseApiResponse
{
    public required List<ProviderUsageStat> Totals { get; init; }
    public required List<ProviderUsageStatDaily> DailyBuckets { get; init; }
}
