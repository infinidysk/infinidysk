namespace NzbWebDAV.Services;

public enum ArrInstanceHealthStatus
{
    Pending,
    Healthy,
    Degraded,
    Offline,
}

public sealed record ArrHealthSnapshot
{
    public required string InstanceKey { get; init; }
    public required string DisplayName { get; init; }
    public required string AppType { get; init; }
    public required string Host { get; init; }
    public ArrInstanceHealthStatus Status { get; init; }
    public int QueueCount { get; init; }
    public int AwaitingCount { get; init; }
    public bool HasWarnings { get; init; }
    public bool HasErrors { get; init; }
    public long? LastImportAtMs { get; init; }
    public DateTimeOffset? LastPolledAt { get; init; }
    public string? LastError { get; init; }
    public long? MedianHandoffMs30d { get; init; }
    public int MedianSampleCount30d { get; init; }
    public IReadOnlyList<ArrAwaitingSnapshot> Awaiting { get; init; } = [];
}

public sealed record ArrAwaitingSnapshot
{
    public string? Title { get; init; }
    public Guid? DownloadId { get; init; }
    public DateTime? CreatedAt { get; init; }
    public string? TrackedDownloadState { get; init; }
    public string? StatusReason { get; init; }
}
