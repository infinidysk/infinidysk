namespace NzbWebDAV.Database.Models.Metrics;

public class ThroughputMinute
{
    public long Minute { get; set; }
    public long BytesServed { get; set; }
    public long BytesFetched { get; set; }
    public long Articles { get; set; }
    public long Misses { get; set; }
    public long Errors { get; set; }
    public int ActiveReadsMax { get; set; }
}
