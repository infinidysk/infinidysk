namespace NzbWebDAV.Api.Controllers.SearchFileInArr;

public sealed class SearchFileInArrResponse : BaseApiResponse
{
    public required Guid DavItemId { get; init; }
    public required string Outcome { get; init; }
    public required IReadOnlyList<Result> Results { get; init; }
    public sealed record Result(string AppType, string InstanceName, IReadOnlyList<int> MediaIds,
        int? CommandId, string State, string? Error);
}