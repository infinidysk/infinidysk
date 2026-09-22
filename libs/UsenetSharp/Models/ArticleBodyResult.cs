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

    /// <summary>The consumer abandoned the body; the connection must be replaced without penalizing provider health.</summary>
    Discarded,
}

/// <summary>Failure reasons are diagnostic text; connection reuse and provider health follow the typed result.</summary>
public delegate void ArticleBodyCompletionHandler(
    ArticleBodyResult result,
    string? failureReason = null);
