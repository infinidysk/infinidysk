namespace NzbWebDAV.Database.Models;

public class HistoryItem
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string FileName { get; set; } = null!;
    public string JobName { get; set; } = null!;
    public string Category { get; set; } = null!;
    public DownloadStatusOption DownloadStatus { get; set; }
    public long TotalSegmentBytes { get; set; }
    public int DownloadTimeSeconds { get; set; }
    public string? FailMessage { get; set; }
    public Guid? DownloadDirId { get; set; }
    public Guid? NzbBlobId { get; set; }
    public string? IndexerName { get; set; }
    public string? ContentGroupKey { get; set; }
    public DateTimeOffset? LastPlayedAt { get; set; }

    /// <summary>
    /// The SAB <c>nzo_id</c> known to represent this release in an external Arr instance.
    /// This is provenance used for validated Arr history lookup. It is not a blob key,
    /// a foreign key to InfiniDysk history, or proof by itself that an Arr instance
    /// still owns the media.
    /// </summary>
    public Guid? ArrDownloadId { get; set; }

    public enum DownloadStatusOption
    {
        Completed = 1,
        Failed = 2,
    }
}
