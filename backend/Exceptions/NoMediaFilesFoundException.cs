namespace NzbWebDAV.Exceptions;

public class NoMediaFilesFoundException(string message) : NonRetryableDownloadException(message)
{
}
