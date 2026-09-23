namespace NzbWebDAV.Database.Models.Metrics;

/// <summary>Global per-hour fetch-rate peak; never pruned so all-time windows stay exact.</summary>
public class ThroughputHourly
{
    public long Hour { get; set; }
    public long PeakFetchBytesPerSec { get; set; }
}
