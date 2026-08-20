namespace NzbWebDAV.Database.Models.Metrics;

/// <summary>
/// One successful Arr DownloadFolderImported event correlated with an InfiniDysk
/// history item (Guid downloadId). Stored in the metrics database so the Overview
/// Arr Health widget never queries Arr APIs or the operational DB.
/// </summary>
public class ArrImportEvent
{
    public long Id { get; set; }
    public string InstanceKey { get; set; } = null!;
    public int ArrRecordId { get; set; }
    public Guid DownloadId { get; set; }
    public long ImportedAtMs { get; set; }
    public long? HandoffMs { get; set; }
    public string? Title { get; set; }
}
