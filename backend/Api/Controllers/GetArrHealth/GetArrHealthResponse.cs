namespace NzbWebDAV.Api.Controllers.GetArrHealth;

public class GetArrHealthResponse : BaseApiResponse
{
    public bool Configured { get; init; }
    public ArrHealthSummary Summary { get; init; } = new();
    public List<ArrHealthInstanceRow> Instances { get; init; } = [];
    public List<ArrAwaitingItem> Awaiting { get; init; } = [];

    public class ArrHealthSummary
    {
        public int InstancesOnline { get; init; }
        public int InstancesTotal { get; init; }
        public int ImportsCompleted { get; init; }
        public long? MedianHandoffMs { get; init; }
        public long? P95HandoffMs { get; init; }
        public int AwaitingImport { get; init; }
        public int AwaitingShown { get; init; }
        public int Degraded { get; init; }
    }

    public class ArrHealthInstanceRow
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string AppType { get; init; } = "";
        public string Host { get; init; } = "";
        public string Status { get; init; } = "pending";
        public int Imports { get; init; }
        public long? MedianHandoffMs { get; init; }
        public long? P95HandoffMs { get; init; }
        public int QueueCount { get; init; }
        public int AwaitingCount { get; init; }
        public bool HasWarnings { get; init; }
        public bool HasErrors { get; init; }
        public long? LastImportAtMs { get; init; }
        public string? LastError { get; init; }
    }

    public class ArrAwaitingItem
    {
        public string? Title { get; init; }
        public Guid? DownloadId { get; init; }
        public string InstanceKey { get; init; } = "";
        public string InstanceName { get; init; } = "";
        public long? WaitingMs { get; init; }
        public bool IsUnusual { get; init; }
        public string? TrackedDownloadState { get; init; }
        public string? StatusReason { get; init; }
    }
}
