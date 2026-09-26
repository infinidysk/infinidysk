namespace NzbWebDAV.Exceptions;

/// <summary>
/// Thrown when an NNTP operation has no enabled provider to run against. This is an
/// instance-wide configuration state, not a fact about the article or file being read,
/// so callers must not record it as a per-item failure. Derives from
/// <see cref="InvalidOperationException"/> so existing handlers keep catching it.
/// </summary>
public class NoUsenetProvidersConfiguredException()
    : InvalidOperationException("There are no usenet providers configured.");
