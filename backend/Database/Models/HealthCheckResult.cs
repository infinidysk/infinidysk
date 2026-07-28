namespace NzbWebDAV.Database.Models;

public class HealthCheckResult
{
    public Guid Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid DavItemId { get; init; }
    public string Path { get; init; } = null!;
    public HealthResult Result { get; init; }
    public RepairAction RepairStatus { get; set; }
    public string? Message { get; set; }

    public enum HealthResult
    {
        Healthy = 0,
        Unhealthy = 1,
    }

    public enum RepairAction
    {
        None = 0,
        Repaired = 1,
        Deleted = 2,
        ActionNeeded = 3,

        /// <summary>Missing articles stayed within tolerance, so no repair was attempted.</summary>
        Degraded = 4,
    }
}
