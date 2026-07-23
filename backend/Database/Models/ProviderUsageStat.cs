namespace NzbWebDAV.Database.Models;

public class ProviderUsageStat
{
    public Guid ProviderId { get; set; }
    public string ProviderHost { get; set; } = null!;
    public long BytesDownloaded { get; set; }
    public long ArticlesNotFoundCount { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
}
