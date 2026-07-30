namespace NzbWebDAV.Exceptions;

/// <summary>
/// Signals that work captured an NNTP client generation which has since retired.
/// This is a lifecycle outcome, not a provider/network failure, so callers must not
/// continue through the other providers from the same retired generation.
/// </summary>
public sealed class NntpClientRetiredException(string message, Exception innerException)
    : IOException(message, innerException);
