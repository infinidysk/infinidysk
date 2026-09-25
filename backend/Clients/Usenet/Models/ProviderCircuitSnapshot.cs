using NzbWebDAV.Models;

namespace NzbWebDAV.Clients.Usenet.Models;

public enum ProviderCircuitState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>Read-only circuit breaker view for APIs and live dashboards.</summary>
public sealed record ProviderCircuitBreakerSnapshot(
    ProviderCircuitState State,
    int? CooldownRemainingSeconds,
    string? LastFailureReason,
    long TripCount,
    long ConsecutiveTrips,
    long FailureCount,
    long ArticleMissCount);

/// <summary>Per configured provider account, keyed by metrics ProviderId.</summary>
public sealed record ProviderCircuitRuntimeSnapshot(
    string MetricsKey,
    string Host,
    ProviderType ProviderType,
    ProviderCircuitBreakerSnapshot Breaker);
