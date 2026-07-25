namespace NzbWebDAV.Exceptions;

public class NonRetryableDownloadException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
}
