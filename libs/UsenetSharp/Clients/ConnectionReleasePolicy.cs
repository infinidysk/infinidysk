namespace UsenetSharp.Clients;

/// <summary>
/// Controls how an operation releases the connection when protocol data
/// remains unread (probe completion or cancellation).
/// </summary>
public enum ConnectionReleasePolicy
{
    /// <summary>
    /// Drain the remaining protocol data (bounded by
    /// <see cref="UsenetClientOptions.AbandonedBodyDrainLimit"/>) so the
    /// connection stays reusable. Costs up to the remaining article size.
    /// </summary>
    DrainToReuse,

    /// <summary>
    /// Mark the connection unusable immediately; the owner is expected to
    /// dispose and reconnect. Fastest release; costs one connection.
    /// </summary>
    AbandonConnection,
}
