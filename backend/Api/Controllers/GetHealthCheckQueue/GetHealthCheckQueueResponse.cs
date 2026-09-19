namespace NzbWebDAV.Api.Controllers.GetHealthCheckQueue;

public class GetHealthCheckQueueResponse : BaseApiResponse
{
    public List<HealthCheckQueueItem> Items { get; init; } = [];
    public int UncheckedCount { get; init; }
    public HealthCheckScheduleStatus? Schedule { get; init; }

    public class HealthCheckScheduleStatus
    {
        public required string TimeZoneId { get; init; }
        public required bool ChecksOpen { get; init; }
        public required bool RepairsOpen { get; init; }
        public DateTimeOffset? NextChecksChange { get; init; }
        public DateTimeOffset? NextRepairsChange { get; init; }
        public required int PendingRepairCount { get; init; }
        public required bool ManualRunActive { get; init; }
    }

    public class HealthCheckQueueItem
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required DateTimeOffset? ReleaseDate { get; init; }
        public required DateTimeOffset? LastHealthCheck { get; init; }
        public required DateTimeOffset? NextHealthCheck { get; init; }
        public int? Progress { get; init; }
    }
}
