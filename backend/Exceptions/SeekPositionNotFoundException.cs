namespace NzbWebDAV.Exceptions;

public class SeekPositionNotFoundException(string message, Exception? innerException = null)
    : NonRetryableDownloadException(message, innerException)
{
}

/// <summary>A BODY's yEnc header disagrees with the recorded per-segment byte ranges.</summary>
public sealed class SegmentGeometryMismatchException(string message)
    : SeekPositionNotFoundException(message)
{
}
