using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Queue;

/// <summary>
/// Distinguishes how an NZB entered the queue so Arr download provenance is
/// captured only for external SAB adds, never inferred from optional fields.
/// </summary>
internal enum NzbSubmissionOrigin
{
    Internal,
    ExternalSabAdd,
    HistoryRetry,
}

/// <summary>
/// Transport-neutral NZB enqueue request. SAB and WebDAV adapters map into this
/// before calling <see cref="NzbSubmissionService"/>.
/// </summary>
public sealed class NzbSubmissionRequest
{
    public Guid? NzoId { get; init; }
    public bool ReplaceExistingQueueItem { get; init; } = true;
    public required string FileName { get; init; }
    public required Stream NzbFileStream { get; init; }
    public string Category { get; init; } = "";
    public QueueItem.PriorityOption Priority { get; init; }
    public QueueItem.PostProcessingOption PostProcessing { get; init; }
    public DateTime? PauseUntil { get; init; }
    public string? IndexerName { get; init; }
    public string? ContentGroupKey { get; init; }
    public CancellationToken CancellationToken { get; init; }
    internal NzbSubmissionOrigin Origin { get; init; }
    internal Guid? ArrDownloadId { get; init; }
}

public sealed class NzbSubmissionResult
{
    public bool Status { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> NzoIds { get; init; } = [];
}
