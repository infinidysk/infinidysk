namespace NzbWebDAV.Database.Models;

public class CachedEpisode
{
    public Guid Id { get; init; }
    public Guid DavItemId { get; init; }
    public string FilePath { get; init; } = null!;
    public long FileSize { get; set; }
    public DateTimeOffset CachedAt { get; init; }
    public DateTimeOffset LastAccessedAt { get; set; }
    public CacheStatus Status { get; set; }

    public enum CacheStatus
    {
        Prefetching = 1,
        Complete = 2,
        Failed = 3,
    }
}
