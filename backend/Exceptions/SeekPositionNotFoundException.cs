namespace NzbWebDAV.Exceptions;

public class SeekPositionNotFoundException(string message, Exception? innerException = null)
    : NonRetryableDownloadException(message, innerException)
{
}
