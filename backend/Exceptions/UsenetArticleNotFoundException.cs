namespace NzbWebDAV.Exceptions;

public class UsenetArticleNotFoundException(string segmentId, string? serverResponse = null)
    : NonRetryableDownloadException(BuildMessage(segmentId, serverResponse))
{
    public string SegmentId => segmentId;
    public long? ProviderGeneration { get; set; }

    private static string BuildMessage(string segmentId, string? serverResponse)
    {
        return serverResponse is null
            ? $"Article with message-id {segmentId} not found."
            : $"Article with message-id {segmentId} not found. Server responded: {serverResponse}";
    }
}
