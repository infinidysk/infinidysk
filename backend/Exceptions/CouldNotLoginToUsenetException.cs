namespace NzbWebDAV.Exceptions;

public class CouldNotLoginToUsenetException(string message, Exception? innerException = null, int? responseCode = null)
    : RetryableDownloadException(message, innerException)
{
    /// <summary>The NNTP response code (e.g. 502), when the server returned one.</summary>
    public int? ResponseCode { get; } = responseCode;
}
