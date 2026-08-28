namespace NzbWebDAV.Api.Controllers.RemoveMissingPayloads;

public sealed class RemoveMissingPayloadsTaskResponse : BaseApiResponse
{
    public required string? Message { get; init; }
    public string? PreviewToken { get; init; }
}
