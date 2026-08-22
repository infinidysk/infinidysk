namespace NzbWebDAV.Clients.Usenet.Models;

public enum ProviderCircuitTransitionState
{
    Open,
    Closed,
}

/// <summary>Connection-pool state observed when a circuit transition was recorded.</summary>
public sealed record ProviderCircuitPoolDiagnostics(
    int LiveConnections,
    int IdleConnections,
    int ActiveConnections);

public sealed record ProviderCircuitTransition(
    ProviderCircuitTransitionState State,
    long AtUnixMilliseconds,
    TimeSpan? Cooldown,
    string? FailureReason = null,
    ProviderCircuitPoolDiagnostics? Pool = null);
