namespace NzbWebDAV.Exceptions;

public class UsenetArticleNotFoundException(string segmentId, string? serverResponse = null)
    : NonRetryableDownloadException(BuildMessage(segmentId, serverResponse))
{
    public string SegmentId => segmentId;
    public long? ProviderGeneration { get; set; }

    /// <summary>
    /// Why this miss does not prove the article is gone, or null when every enabled provider
    /// source answered with a definitive miss. An inconclusive miss must not be recorded as
    /// missing-article evidence: no fail-fast seed, no repair, and no persisted hole.
    /// </summary>
    public string? InconclusiveReason { get; set; }

    private static string BuildMessage(string segmentId, string? serverResponse)
    {
        return serverResponse is null
            ? $"Article with message-id {segmentId} not found."
            : $"Article with message-id {segmentId} not found. Server responded: {serverResponse}";
    }
}
