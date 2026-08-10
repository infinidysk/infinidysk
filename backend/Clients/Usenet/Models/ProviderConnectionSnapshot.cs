using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Models;

namespace NzbWebDAV.Clients.Usenet.Models;

/// <summary>
/// Live connection-pool state plus lifetime churn for one configured provider
/// account, keyed by metrics ProviderId.
/// </summary>
public sealed record ProviderConnectionSnapshot(
    string MetricsKey,
    string Host,
    ProviderType ProviderType,
    int LiveConnections,
    int IdleConnections,
    int ActiveConnections,
    int AvailableConnections,
    int PendingSelections,
    ConnectionPoolChurn Churn,
    int? LearnedConnectionLimit,
    int EffectiveMaxConnections);
