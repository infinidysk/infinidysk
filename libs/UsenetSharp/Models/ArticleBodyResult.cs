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
