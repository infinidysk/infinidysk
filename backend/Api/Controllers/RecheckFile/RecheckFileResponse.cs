namespace NzbWebDAV.Api.Controllers.RecheckFile;

public sealed class RecheckFileResponse : BaseApiResponse
{
    public required Guid DavItemId { get; init; }
    public required string State { get; init; }
}