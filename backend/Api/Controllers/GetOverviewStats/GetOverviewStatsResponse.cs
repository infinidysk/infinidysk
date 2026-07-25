namespace NzbWebDAV.Api.Controllers.GetOverviewStats;

public class GetOverviewStatsResponse
{
    public string Window { get; init; } = "24h";

    /// <summary>Which section groups were populated in this response (window, detail, static).</summary>
    public List<string> IncludedSections { get; init; } = new();

    public LiveTiles Tiles { get; init; } = new();
    public List<ThroughputPoint> Throughput { get; init; } = new();
    /// <summary>Chart bucket width in ms for normalizing throughput totals into rates.</summary>
    public long ThroughputBucketSizeMs { get; init; }
    public long TotalArticles { get; init; }
    public long TotalMisses { get; init; }
    public long TotalErrors { get; init; }
    public long TotalBytesFetched { get; init; }
    public List<ProviderRow> Providers { get; init; } = new();
    public CatalogueBlock Catalogue { get; init; } = new();
    public SessionsBlock Sessions { get; init; } = new();

    // Goated additions
    public HeatmapBlock Heatmap { get; init; } = new();
    public LatencyBlock Latency { get; init; } = new();
    public List<ErrorSlice> Errors { get; init; } = new();
    public List<IndexerRow> Indexers { get; init; } = new();
    public List<IndexerApiUsageRow> IndexerApiUsage { get; init; } = new();
    public LifetimeBlock Lifetime { get; init; } = new();
    public RecordsBlock Records { get; init; } = new();
    public FailoverBlock Failover { get; init; } = new();
    public MetricsHealthBlock MetricsHealth { get; init; } = new();

    public class LiveTiles
    {
        public int ActiveReads { get; init; }
        public long ArticlesPerMinute { get; init; }
        public long ErrorsPerMinute { get; init; }
        public long BytesServedPerMinute { get; init; }
    }

    public class MetricsHealthBlock
    {
        public long Queued { get; init; }
        public long Dropped { get; init; }
        public long LastSuccessfulFlushAtMs { get; init; }
        public string? LastFlushError { get; init; }
    }

    public class ThroughputPoint
    {
        public long Bucket { get; init; }
        public long Articles { get; init; }
        public long Misses { get; init; }
        public long Errors { get; init; }
        public long BytesServed { get; init; }
        /// <summary>Bytes downloaded from Usenet providers in this bucket.</summary>
        public long BytesFetched { get; init; }
    }

    public class ProviderRow
    {
        public string Provider { get; init; } = "";
        public string? Nickname { get; init; }
        public long Articles { get; init; }
        public long BytesFetched { get; init; }
        public long Errors { get; init; }
        public long Retries { get; init; }
        /// <summary>
        /// Mean duration of successful (Ok) fetches only. Includes connection-pool wait
        /// inside the provider call; not pure wire RTT. Misses/errors are excluded.
        /// </summary>
        public double AvgDurationMs { get; init; }
        public double ErrorRate { get; init; }
        public List<long> Spark { get; init; } = new();
        /// <summary>Hard fetch failures per spark bucket (same sizing as <see cref="Spark"/>).</summary>
        public List<long> ErrorSpark { get; init; } = new();
        /// <summary>Segment-fetch retry counts per spark bucket (same sizing as <see cref="Spark"/>).</summary>
        public List<long> RetrySpark { get; init; } = new();
        /// <summary>Percentage of each spark bucket spent with the provider circuit open.</summary>
        public List<int> OutageSpark { get; set; } = new();
        public string CircuitState { get; init; } = "closed";
        public int? CooldownRemainingSeconds { get; init; }
        public string? LastFailureReason { get; init; }
        public long TripCount { get; init; }
        public long FailureCount { get; init; }
        public long ArticleMissCount { get; init; }
    }

    public class CatalogueBlock
    {
        public long FileCount { get; init; }
        public long TotalBytes { get; init; }
        public long LargestFileBytes { get; init; }
        public long AddedLast7Days { get; init; }
    }

    public class SessionsBlock
    {
        public long Count { get; init; }
        public long TotalBytesServed { get; init; }
        public long AvgDurationMs { get; init; }
        public long LongestDurationMs { get; init; }
        public long BiggestReadBytes { get; init; }
    }

    public class HeatmapBlock
    {
        public long MaxCell { get; init; }
        public string Mode { get; init; } = "week";
        public long WindowStartMs { get; init; }
        public long WindowEndMs { get; init; }
        public long BucketSizeMs { get; init; }
        public List<HeatmapCell> Cells { get; init; } = new();
    }

    public class HeatmapCell
    {
        public long Bucket { get; init; }
        public long Count { get; init; }
    }

    /// <summary>Fetch-duration percentiles + log-scale histogram for the window.</summary>
    public class LatencyBlock
    {
        public int P50Ms { get; init; }
        public int P95Ms { get; init; }
        public int P99Ms { get; init; }
        public int Samples { get; init; }
        public List<LatencyBucket> Buckets { get; init; } = new();
    }

    public class LatencyBucket
    {
        public int LoMs { get; init; }
        public int HiMs { get; init; }
        public long Count { get; init; }
    }

    /// <summary>Share of each fetch error type, for the donut.</summary>
    public class ErrorSlice
    {
        public string Status { get; init; } = "";
        public long Count { get; init; }
    }

    /// <summary>Per-indexer aggregate over the last 30 days from HistoryItems.</summary>
    public class IndexerRow
    {
        public string Name { get; init; } = "";
        public long Completed { get; init; }
        public long Failed { get; init; }
        public long BytesCompleted { get; init; }
        public int AvgSeconds { get; init; }
        public double SuccessRate { get; init; }
    }

    /// <summary>
    /// Per-indexer hit-limit usage in the current reset window. ApiHitLimit and
    /// DownloadHitLimit are null when no limit is configured. ResetAtMs is the unix-ms
    /// timestamp of the next window boundary — either the configured reset hour
    /// (UTC) or now+24h for the rolling-window case.
    /// </summary>
    public class IndexerApiUsageRow
    {
        public string Name { get; init; } = "";
        public int ApiHits { get; init; }
        public int? ApiHitLimit { get; init; }
        public int DownloadHits { get; init; }
        public int? DownloadHitLimit { get; init; }
        public long ResetAtMs { get; init; }
        public int? ResetHourUtc { get; init; }
    }

    /// <summary>
    /// All-time totals across every minute the metrics database has retained. Values
    /// only grow; the dashboard renders them as the big "your forever stats" tiles.
    /// </summary>
    public class LifetimeBlock
    {
        public long BytesFetched { get; init; }
        public long BytesRead { get; init; }
        public long Articles { get; init; }
        public long ReadSessions { get; init; }
        public long ReadSeconds { get; init; }
        public long? FirstSeenAt { get; init; }
    }

    /// <summary>
    /// Personal-best records — "your busiest day", "your busiest hour". Bytes-fetched
    /// here is what the providers actually delivered (downstream of the byte tracker).
    /// </summary>
    public class RecordsBlock
    {
        public long BestDayBytes { get; init; }
        public long? BestDayAt { get; init; }
        public long BestHourBytes { get; init; }
        public long? BestHourAt { get; init; }
    }

    public class FailoverBlock
    {
        public long ArticlesRecovered { get; init; }
        public long? PreviousArticlesRecovered { get; init; }
        public long SegmentsCovered { get; init; }
        public long ReadsSaved { get; init; }
        public long ReadSessions { get; init; }
        public long TotalArticles { get; init; }
        public long BucketSizeMs { get; init; }
        public List<FailoverProvider> RescuedBy { get; init; } = new();
        public List<FailoverFrom> RescuedFrom { get; init; } = new();
        public List<FailoverReason> Reasons { get; init; } = new();
        public List<FailoverBucket> Buckets { get; init; } = new();
    }

    public class FailoverProvider
    {
        public string Provider { get; init; } = "";
        public string? Nickname { get; init; }
        public long Saves { get; init; }
    }

    public class FailoverFrom
    {
        public string Provider { get; init; } = "";
        public string? Nickname { get; init; }
        public long Misses { get; init; }
    }

    public class FailoverReason
    {
        public string Status { get; init; } = "";
        public long Count { get; init; }
    }

    public class FailoverBucket
    {
        public long Bucket { get; init; }
        public List<long> Counts { get; init; } = new();
    }
}
