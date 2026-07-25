namespace NzbWebDAV.Database.Models.Metrics;

public class SegmentFetch
{
    public long Id { get; set; }
    public long At { get; set; }
    public string Provider { get; set; } = null!;
    public Guid? ReadSessionId { get; set; }
    public Guid? QueueItemId { get; set; }
    public long Bytes { get; set; }
    public int DurationMs { get; set; }
    public FetchStatus Status { get; set; }
    public int Retries { get; set; }

    public enum FetchStatus
    {
        Ok = 0,
        Missing = 1,
        Timeout = 2,
        Corrupt = 3,
        Auth = 4,
        Network = 5,
        Protocol = 6,
        Other = 9,
    }
}
