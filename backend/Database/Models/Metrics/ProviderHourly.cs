namespace NzbWebDAV.Database.Models.Metrics;

public class ProviderHourly
{
    public long Hour { get; set; }
    public string Provider { get; set; } = null!;
    public long Articles { get; set; }
    public long BytesFetched { get; set; }
    public long Misses { get; set; }
    public long Errors { get; set; }
    public long Retries { get; set; }
    public long FailoverSaves { get; set; }
    public long SumDurationMs { get; set; }
    public int? P95DurationMs { get; set; }
}
