namespace NzbWebDAV.Api.Controllers.GetLibraryFileDetails;

public class GetLibraryFileDetailsResponse : BaseApiResponse
{
    public required string DavItemId { get; init; }
    public required string Name { get; init; }
    public required string ContentPath { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }
    public DateTimeOffset? LastHealthCheck { get; init; }
    public DateTimeOffset? NextHealthCheck { get; init; }
    public bool HealthRepairPending { get; init; }
    public string? HistoryItemId { get; init; }
    public LibraryFileDetailsHealth? LatestHealth { get; init; }
    public List<LibraryFileDetailsMapping> Mappings { get; init; } = [];

    public class LibraryFileDetailsHealth
    {
        public required string Result { get; init; }
        public required string RepairStatus { get; init; }
        public string? Message { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    public class LibraryFileDetailsMapping
    {
        public required string LinkPath { get; init; }
        public required string TargetText { get; init; }
        public required string MappingType { get; init; }
        public required string Status { get; init; }
    }
}
