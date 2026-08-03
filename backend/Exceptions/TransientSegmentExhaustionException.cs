namespace NzbWebDAV.Exceptions;

public class TransientSegmentExhaustionException(string message, Exception? innerException = null)
    : RetryableDownloadException(message, innerException);
