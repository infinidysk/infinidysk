namespace NzbWebDAV.Api.Controllers.RemoveMissingPayloads;

public sealed class RemoveMissingPayloadsAuditResponse : BaseApiResponse
{
    public required string Report { get; init; }
}
