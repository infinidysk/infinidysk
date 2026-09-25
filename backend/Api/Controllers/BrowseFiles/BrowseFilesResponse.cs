using NzbWebDAV.Api.Controllers.GetHealthCheckQueue;

namespace NzbWebDAV.Api.Controllers.BrowseFiles;

public sealed class BrowseFilesResponse : BaseApiResponse
{
    public required string Mode { get; init; }
    public required string ScopePath { get; init; }
    public required string ParentPath { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required int TotalRows { get; init; }
    public required int MatchingFileCount { get; init; }
    public required bool HasMore { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required string LibraryScanState { get; init; }
    public required DateTimeOffset? LibraryScannedAt { get; init; }
    public required string? LibraryError { get; init; }
    public required GetHealthCheckQueueResponse.HealthCheckScheduleStatus Schedule { get; init; }
    public required IReadOnlyList<FileRow> Rows { get; init; }

    public sealed class FileRow
    {
        public required string Key { get; init; }
        public required Guid? Id { get; init; }
        public required Guid? ParentId { get; init; }
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required bool IsDirectory { get; init; }
        public required bool HasChildren { get; init; }
        public required long? Size { get; init; }
        public required int? SubType { get; init; }
        public required DateTimeOffset? AddedAt { get; init; }
        public required DateTimeOffset? ReleaseDate { get; init; }
        public required DateTimeOffset? LastHealthCheck { get; init; }
        public required DateTimeOffset? NextCheckAt { get; init; }
        public required string? Health { get; init; }
        public required string? ScanState { get; init; }
        public required int? Progress { get; init; }
        public required Guid? HealthResultId { get; init; }
        public required DateTimeOffset? HealthResultAt { get; init; }
        public required int? RepairAction { get; init; }
        public required string? HealthMessage { get; init; }
        public required Guid? HistoryItemId { get; init; }
        public required string? JobName { get; init; }
        public required string? NzbFileName { get; init; }
        public required string? Category { get; init; }
        public required string? IndexerName { get; init; }
        public required DateTimeOffset? LastPlayedAt { get; init; }
        public required Guid? NzbBlobId { get; init; }
        public required string? LibraryState { get; init; }
        public required int LibraryLinkCount { get; init; }
        public required IReadOnlyList<string> LibraryPaths { get; init; }
        public required bool CanRecheck { get; init; }
        public required string? RecheckDisabledReason { get; init; }
        public required bool CanDelete { get; init; }
        public required string? DeleteDisabledReason { get; init; }
        public required bool CanSearchArr { get; init; }
        public required string? SearchArrDisabledReason { get; init; }
    }
}