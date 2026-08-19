namespace NzbWebDAV.Database.Models;

public class Par2RepairJob
{
    public Guid Id { get; set; }
    public Guid DavItemId { get; set; }
    public string Path { get; set; } = null!;
    public RepairJobState State { get; set; }
    public string[] MissingSegmentIds { get; set; } = Array.Empty<string>();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? FailureReason { get; set; }
    public long BytesRead { get; set; }
    public int SlicesReconstructed { get; set; }

    public enum RepairJobState
    {
        Queued = 0,
        Running = 1,
        Succeeded = 2,
        Failed = 3,
        Infeasible = 4,
    }
}
