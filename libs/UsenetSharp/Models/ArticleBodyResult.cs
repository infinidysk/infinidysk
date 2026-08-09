namespace UsenetSharp.Models;

/// <summary>
/// Describes why an NNTP connection became available after a body operation.
/// </summary>
public enum ArticleBodyResult
{
    /// <summary>The requested body was retrieved and the connection is reusable.</summary>
    Retrieved,

    /// <summary>The body was not retrieved because the operation failed.</summary>
    NotRetrieved,

    /// <summary>The server cleanly reported that the requested article was not found.</summary>
    NotFound,

    /// <summary>The caller cancelled the operation and the connection was successfully drained.</summary>
    Cancelled,
}

/// <summary>
/// Completion callback for body operations. <paramref name="failureReason"/> carries a
/// short classification of the transport failure (exception type, socket error) when
/// <paramref name="result"/> is <see cref="ArticleBodyResult.NotRetrieved"/>, so callers
/// recording circuit-breaker or metrics reasons can name the root cause. It is null for
/// clean outcomes and for cancellations.
/// </summary>
public delegate void ArticleBodyCompletionHandler(
    ArticleBodyResult result,
    string? failureReason = null);
