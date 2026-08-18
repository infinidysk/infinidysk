namespace NzbWebDAV.Database.Models;

/// <summary>
/// A definitive, provider- or storage-group-scoped NNTP article miss.
/// </summary>
public sealed class ArticleMissCacheEntry
{
    public required string CacheKey { get; init; }
    public long ConfirmedAtUnix { get; set; }
}
