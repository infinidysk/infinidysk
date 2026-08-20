using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Api.Controllers.GetOverviewStats;

internal static class ProviderOverviewRowMapper
{
    public static ProviderOverviewRow ToMetrics(GetOverviewStatsResponse.ProviderRow row) => new()
    {
        Provider = row.Provider,
        Nickname = row.Nickname,
        Articles = row.Articles,
        BytesFetched = row.BytesFetched,
        Errors = row.Errors,
        Retries = row.Retries,
        SpeedMbPerSec = row.SpeedMbPerSec,
        SpeedSpark = row.SpeedSpark,
        AvgDurationMs = row.AvgDurationMs,
        ErrorRate = row.ErrorRate,
        Spark = row.Spark,
        ErrorSpark = row.ErrorSpark,
        RetrySpark = row.RetrySpark,
        OutageSpark = row.OutageSpark,
        CircuitState = row.CircuitState,
        CooldownRemainingSeconds = row.CooldownRemainingSeconds,
        LastFailureReason = row.LastFailureReason,
        TripCount = row.TripCount,
        FailureCount = row.FailureCount,
        ArticleMissCount = row.ArticleMissCount,
    };

    public static GetOverviewStatsResponse.ProviderRow ToApi(ProviderOverviewRow row) => new()
    {
        Provider = row.Provider,
        Nickname = row.Nickname,
        Articles = row.Articles,
        BytesFetched = row.BytesFetched,
        Errors = row.Errors,
        Retries = row.Retries,
        SpeedMbPerSec = row.SpeedMbPerSec,
        SpeedSpark = row.SpeedSpark,
        AvgDurationMs = row.AvgDurationMs,
        ErrorRate = row.ErrorRate,
        Spark = row.Spark,
        ErrorSpark = row.ErrorSpark,
        RetrySpark = row.RetrySpark,
        OutageSpark = row.OutageSpark,
        CircuitState = row.CircuitState,
        CooldownRemainingSeconds = row.CooldownRemainingSeconds,
        LastFailureReason = row.LastFailureReason,
        TripCount = row.TripCount,
        FailureCount = row.FailureCount,
        ArticleMissCount = row.ArticleMissCount,
    };
}
