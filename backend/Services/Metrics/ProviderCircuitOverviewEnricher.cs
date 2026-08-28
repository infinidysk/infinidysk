using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Services.Metrics;

internal static class ProviderCircuitOverviewEnricher
{
    internal static List<ProviderOverviewRow> EnrichProviders(
        IReadOnlyList<ProviderOverviewRow> providers,
        IReadOnlyList<ProviderCircuitRuntimeSnapshot> runtimeSnapshots,
        IReadOnlyDictionary<string, string?> labelsByMetricsKey)
    {
        var byKey = providers.ToDictionary(p => p.Provider, StringComparer.Ordinal);
        foreach (var runtime in runtimeSnapshots)
        {
            var fields = ToRowFields(runtime.Breaker);
            if (byKey.TryGetValue(runtime.MetricsKey, out var existing))
            {
                byKey[runtime.MetricsKey] = new ProviderOverviewRow
                {
                    Provider = existing.Provider,
                    Nickname = existing.Nickname,
                    Articles = existing.Articles,
                    BytesFetched = existing.BytesFetched,
                    Errors = existing.Errors,
                    Retries = existing.Retries,
                    SpeedMbPerSec = existing.SpeedMbPerSec,
                    SpeedSpark = existing.SpeedSpark,
                    SpeedSeries = existing.SpeedSeries,
                    AvgDurationMs = existing.AvgDurationMs,
                    ErrorRate = existing.ErrorRate,
                    Spark = existing.Spark,
                    ErrorSpark = existing.ErrorSpark,
                    RetrySpark = existing.RetrySpark,
                    OutageSpark = existing.OutageSpark,
                    CircuitState = fields.CircuitState,
                    CooldownRemainingSeconds = fields.CooldownRemainingSeconds,
                    LastFailureReason = fields.LastFailureReason,
                    TripCount = fields.TripCount,
                    FailureCount = fields.FailureCount,
                    ArticleMissCount = fields.ArticleMissCount,
                };
                continue;
            }

            byKey[runtime.MetricsKey] = new ProviderOverviewRow
            {
                Provider = runtime.MetricsKey,
                Nickname = labelsByMetricsKey.GetValueOrDefault(runtime.MetricsKey),
                CircuitState = fields.CircuitState,
                CooldownRemainingSeconds = fields.CooldownRemainingSeconds,
                LastFailureReason = fields.LastFailureReason,
                TripCount = fields.TripCount,
                FailureCount = fields.FailureCount,
                ArticleMissCount = fields.ArticleMissCount,
            };
        }

        return byKey.Values
            .OrderByDescending(r => r.Articles)
            .ThenBy(r => r.Nickname ?? r.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static List<object> ToLivePayload(
        IReadOnlyList<ProviderCircuitRuntimeSnapshot> runtimeSnapshots,
        IReadOnlyDictionary<string, string?> labelsByMetricsKey)
    {
        return runtimeSnapshots
            .Select(runtime =>
            {
                var fields = ToRowFields(runtime.Breaker);
                return (object)new
                {
                    provider = runtime.MetricsKey,
                    nickname = labelsByMetricsKey.GetValueOrDefault(runtime.MetricsKey),
                    providerType = runtime.ProviderType.ToString(),
                    circuitState = fields.CircuitState,
                    cooldownRemainingSeconds = fields.CooldownRemainingSeconds,
                    lastFailureReason = fields.LastFailureReason,
                    tripCount = fields.TripCount,
                    failureCount = fields.FailureCount,
                    articleMissCount = fields.ArticleMissCount,
                };
            })
            .ToList();
    }

    private static (
        string CircuitState,
        int? CooldownRemainingSeconds,
        string? LastFailureReason,
        long TripCount,
        long FailureCount,
        long ArticleMissCount) ToRowFields(ProviderCircuitBreakerSnapshot breaker)
    {
        return (
            ToWireState(breaker.State),
            breaker.CooldownRemainingSeconds,
            breaker.LastFailureReason,
            breaker.TripCount,
            breaker.FailureCount,
            breaker.ArticleMissCount);
    }

    private static string ToWireState(ProviderCircuitState state) => state switch
    {
        ProviderCircuitState.Open => "open",
        ProviderCircuitState.HalfOpen => "halfOpen",
        _ => "closed",
    };
}
