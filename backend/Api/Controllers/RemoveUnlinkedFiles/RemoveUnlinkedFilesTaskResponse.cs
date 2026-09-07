namespace NzbWebDAV.Api.Controllers.RemoveUnlinkedFiles;

public sealed class RemoveUnlinkedFilesTaskResponse : BaseApiResponse
{
    public string? PreviewToken { get; init; }
}