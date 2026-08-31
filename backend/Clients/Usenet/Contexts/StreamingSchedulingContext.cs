namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Token-scoped immutable scheduling input captured when a private producer is created.
/// Stream algorithms consume this value but never query mutable configuration or pools.
/// </summary>
internal sealed record StreamingSchedulingContext
{
    internal required StreamingCapacitySnapshot Snapshot { get; init; }
}
