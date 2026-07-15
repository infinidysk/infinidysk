namespace NzbWebDAV.Exceptions;

public class SampleOnlyReleaseException(string message) : NonRetryableDownloadException(message)
{
}
