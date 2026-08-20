namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Provider overview row owned by Metrics so circuit enrichment does not depend
/// on admin API response types.
/// </summary>
internal sealed class ProviderOverviewRow
{
    public string Provider { get; init; } = "";
    public string? Nickname { get; init; }
    public long Articles { get; init; }
    public long BytesFetched { get; init; }
    public long Errors { get; init; }
    public long Retries { get; init; }
    public double? SpeedMbPerSec { get; set; }
    public List<double> SpeedSpark { get; init; } = [];
    public double AvgDurationMs { get; init; }
    public double ErrorRate { get; init; }
    public List<long> Spark { get; init; } = [];
    public List<long> ErrorSpark { get; init; } = [];
    public List<long> RetrySpark { get; init; } = [];
    public List<int> OutageSpark { get; set; } = [];
    public string CircuitState { get; init; } = "closed";
    public int? CooldownRemainingSeconds { get; init; }
    public string? LastFailureReason { get; init; }
    public long TripCount { get; init; }
    public long FailureCount { get; init; }
    public long ArticleMissCount { get; init; }
}
