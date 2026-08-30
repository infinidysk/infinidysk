namespace NzbWebDAV.Exceptions;

/// <summary>
/// Expected failure while reading an untrusted remote metadata response
/// (Newznab XML or Watchtower list source). Messages are controlled and must not
/// include request URLs, API keys, or body excerpts.
/// </summary>
public abstract class RemoteResponseException(string message, Exception? innerException)
    : Exception(message, innerException);

public sealed class RemoteResponseTooLargeException(
    long maxBytes,
    long? declaredBytes,
    Exception innerException)
    : RemoteResponseException(
        $"Remote response exceeded the configured {maxBytes:N0}-byte limit.",
        innerException)
{
    public long MaxBytes { get; } = maxBytes;
    public long? DeclaredBytes { get; } = declaredBytes;
}

public sealed class RemoteResponseFormatException(string message, Exception innerException)
    : RemoteResponseException(message, innerException);
