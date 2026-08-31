using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Reads configuration, provider state, and active reader count once at stream
/// construction. The returned snapshot is an auditable scheduling hint, not admission.
/// </summary>
internal sealed class StreamingCapacitySnapshotProvider(
    ConfigManager configManager,
    UsenetStreamingClient streamingClient,
    ConcurrentReadTracker concurrentReadTracker)
{
    internal StreamingCapacitySnapshot Capture()
    {
        var isPerStream = configManager.IsMaxDownloadConnectionsPerStream();
        var configuredDownloadBudget = configManager.GetMaxDownloadConnections();
        var configuredPerStreamBudget = configManager.GetMaxDownloadConnectionsPerStreamCount();
        var articleBufferSize = configManager.GetArticleBufferSize();
        var readerCount = Math.Max(1, concurrentReadTracker.GetActiveReaderCount());
        return CreateSnapshot(
            isPerStream,
            configuredDownloadBudget,
            configuredPerStreamBudget,
            readerCount,
            articleBufferSize,
            configManager.GetInFlightArticleBudgetBytes(),
            streamingClient.GetSchedulingProviderSnapshots());
    }

    internal static StreamingCapacitySnapshot CreateSnapshot(
        bool isPerStream,
        int configuredDownloadBudget,
        int configuredPerStreamBudget,
        int readerCount,
        int articleBufferSize,
        long inFlightArticleBudgetBytes,
        IReadOnlyList<StreamingProviderCapacitySnapshot> providers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(configuredDownloadBudget, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuredPerStreamBudget, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(readerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(articleBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(inFlightArticleBudgetBytes);

        var primaryCapacity = SumHealthyPrimaryTransferCapacity(providers);

        if (primaryCapacity <= 0)
        {
            return new StreamingCapacitySnapshot(
                isPerStream,
                configuredDownloadBudget,
                configuredPerStreamBudget,
                readerCount,
                0,
                1,
                articleBufferSize,
                inFlightArticleBudgetBytes,
                StreamingCapacityReason.NoHealthyPrimaryCapacity);
        }

        var sharedCapacity = Math.Min(configuredDownloadBudget, primaryCapacity);
        var target = isPerStream
            ? Math.Min(configuredPerStreamBudget, Math.Max(1, primaryCapacity / readerCount))
            : sharedCapacity / readerCount;

        return new StreamingCapacitySnapshot(
            isPerStream,
            configuredDownloadBudget,
            configuredPerStreamBudget,
            readerCount,
            primaryCapacity,
            Math.Max(1, target),
            articleBufferSize,
            inFlightArticleBudgetBytes,
            StreamingCapacityReason.Ok);
    }

    private static int SumHealthyPrimaryTransferCapacity(
        IReadOnlyList<StreamingProviderCapacitySnapshot> providers)
    {
        var total = 0;
        foreach (var provider in providers)
        {
            if (provider.ProviderType != ProviderType.Pooled ||
                provider.CircuitState != ProviderCircuitState.Closed)
                continue;

            var capacity = provider.AdmissionEffectiveTransferLimit ?? provider.EffectiveMaxConnections;
            if (capacity <= 0)
                continue;

            total = total > int.MaxValue - capacity ? int.MaxValue : total + capacity;
        }

        return total;
    }
}

internal readonly record struct StreamingProviderCapacitySnapshot(
    ProviderType ProviderType,
    ProviderCircuitState CircuitState,
    int EffectiveMaxConnections,
    int? AdmissionEffectiveTransferLimit);
