namespace NzbWebDAV.Api.Controllers.GetLibraryCatalog;

public class GetLibraryCatalogResponse : BaseApiResponse
{
    public List<LibraryCatalogItem> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public DateTimeOffset? IndexScannedAt { get; init; }
    public string? IndexWarning { get; init; }

    public class LibraryCatalogItem
    {
        public required string Kind { get; init; }
        public string? DavItemId { get; init; }
        public required string DisplayName { get; init; }
        public string? ContentPath { get; init; }
        public long? Size { get; init; }
        public int MappingCount { get; init; }
        public required string Health { get; init; }
        public List<LibraryCatalogMapping> Mappings { get; init; } = [];
    }

    public class LibraryCatalogMapping
    {
        public required string LinkPath { get; init; }
        public required string TargetText { get; init; }
        public required string MappingType { get; init; }
        public required string Status { get; init; }
    }
}
