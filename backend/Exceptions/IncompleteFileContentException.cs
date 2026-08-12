namespace NzbWebDAV.Exceptions;

/// <summary>
/// Thrown when a file stream ends before the declared file or range length is satisfied.
/// Continuing would produce a truncated HTTP body and trigger Kestrel's Content-Length mismatch.
/// </summary>
public class IncompleteFileContentException(string filePath, long expectedBytes, long deliveredBytes)
    : NonRetryableDownloadException(BuildMessage(filePath, expectedBytes, deliveredBytes))
{
    public string FilePath { get; } = filePath;
    public long ExpectedBytes { get; } = expectedBytes;
    public long DeliveredBytes { get; } = deliveredBytes;

    private static string BuildMessage(string filePath, long expectedBytes, long deliveredBytes)
    {
        var shortfall = expectedBytes - deliveredBytes;
        return
            $"File \"{filePath}\" ended {shortfall} bytes early " +
            $"(delivered {deliveredBytes} of {expectedBytes} expected bytes).";
    }
}
