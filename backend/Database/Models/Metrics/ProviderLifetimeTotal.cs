namespace NzbWebDAV.Database.Models.Metrics;

/// <summary>
/// Cumulative provider totals folded from pruned <see cref="ProviderHourly"/> rows.
/// Preserves all-time bandwidth and article counts across the hourly retention window.
/// </summary>
public class ProviderLifetimeTotal
{
    public string Provider { get; set; } = null!;
    public long BytesFetched { get; set; }
    public long? PeakBytesPerSec { get; set; }
    public long? ActiveBytes { get; set; }
    public double? ActiveSeconds { get; set; }
    public long Articles { get; set; }
    public long ClientArticles { get; set; }
    public long QueueArticles { get; set; }
    public long Misses { get; set; }
    public long Errors { get; set; }
    public long Retries { get; set; }
    public long SumDurationMs { get; set; }
    public long FailoverSaves { get; set; }
    public long? FirstHour { get; set; }
}
