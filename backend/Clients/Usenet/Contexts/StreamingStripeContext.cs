namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Token-scoped hint for how many pipelined BODY batches a playback stream should
/// interleave. Captured once at stream open; live admission remains authoritative.
/// </summary>
internal sealed record StreamingStripeContext
{
    internal required int StripeCount { get; init; }

    /// <summary>Connections the stream could start now without waiting; null when unknown.</summary>
    internal Func<int>? AvailableConnections { get; init; }
}
