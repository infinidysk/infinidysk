namespace NzbWebDAV.Config;

public sealed class ProwlarrSyncStatus
{
    public long LastAttemptAt { get; set; }
    public long? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int RemoteIndexerCount { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int Skipped { get; set; }
}
