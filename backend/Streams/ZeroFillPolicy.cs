namespace NzbWebDAV.Streams;

/// <summary>
/// How many missing articles a read may replace with zeros before it gives up. A player
/// resynchronizes past a short gap, but a long one is structural damage no decoder recovers
/// from, so the stream fails instead of handing back an unbounded run of silence.
/// </summary>
internal static class ZeroFillPolicy
{
    /// <summary>
    /// A run this long fails the read, so a run one shorter is the most that is ever served.
    /// Health checks apply the same bound, and the two must agree: a file a health check
    /// passes but a read refuses is the worst of both.
    /// </summary>
    public const int MaxConsecutive = 3;
}
