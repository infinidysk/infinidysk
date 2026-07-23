namespace NzbWebDAV.Database.Models;

public class ProviderUsageStatDaily
{
    public DateTimeOffset DateStartInclusive { get; set; }
    public DateTimeOffset DateEndExclusive { get; set; }
    public Guid ProviderId { get; set; }
    public long BytesDownloaded { get; set; }
    public long ArticlesNotFoundCount { get; set; }
}
