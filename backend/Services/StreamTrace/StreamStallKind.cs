namespace NzbWebDAV.Services.StreamTrace;

/// <summary>
/// Stages a ranged read can block on. Accumulated per range so a slow playback
/// says which side was waiting instead of only how long the range took: a range
/// dominated by <see cref="ClientWrite"/> is limited by the player's own link,
/// while <see cref="ConsumerWait"/> with little provider time means prefetch is
/// not running ahead of the consumer.
/// </summary>
public enum StreamStallKind
{
    /// <summary>Waiting for a pooled NNTP connection, including handshake pacing.</summary>
    ConnectionWait,

    /// <summary>Request dispatched until the provider's response header arrived.</summary>
    ProviderWait,

    /// <summary>Reading an article body off the socket into its buffer.</summary>
    BodyDrain,

    /// <summary>Consumer blocked waiting for the next prefetched segment to arrive.</summary>
    ConsumerWait,

    /// <summary>Blocked writing the response body to the HTTP client.</summary>
    ClientWrite,
}
