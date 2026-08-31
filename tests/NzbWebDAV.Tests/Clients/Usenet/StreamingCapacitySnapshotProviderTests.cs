using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class StreamingCapacitySnapshotProviderTests
{
    [Fact]
    public void SharedMode_UsesConservativeIntegerReaderShare()
    {
        var snapshot = Create(isPerStream: false, readers: 2, Provider(20));

        Assert.Equal(20, snapshot.EffectivePrimaryTransferCapacity);
        Assert.Equal(10, snapshot.EffectiveStreamConnectionTarget);
    }

    [Fact]
    public void PerStreamMode_UsesPresetCeilingAndProviderShare()
    {
        var snapshot = Create(isPerStream: true, readers: 2, Provider(20));

        Assert.Equal(15, snapshot.ConfiguredPerStreamBudget);
        Assert.Equal(10, snapshot.EffectiveStreamConnectionTarget);
    }

    [Fact]
    public void BackupAndOpenProvidersDoNotInflatePrimaryCapacity()
    {
        var snapshot = Create(
            false,
            1,
            Provider(10),
            new StreamingProviderCapacitySnapshot(
                ProviderType.BackupOnly, ProviderCircuitState.Closed, 50, null),
            new StreamingProviderCapacitySnapshot(
                ProviderType.Pooled, ProviderCircuitState.Open, 50, null));

        Assert.Equal(10, snapshot.EffectivePrimaryTransferCapacity);
        Assert.Equal(10, snapshot.EffectiveStreamConnectionTarget);
    }

    [Fact]
    public void SplitAdmissionUsesEffectiveTransferLimit()
    {
        var snapshot = Create(false, 1, Provider(20, transferLimit: 8));

        Assert.Equal(8, snapshot.EffectivePrimaryTransferCapacity);
        Assert.Equal(8, snapshot.EffectiveStreamConnectionTarget);
    }

    [Fact]
    public void NoHealthyPrimaryReturnsDegradedTargetOne()
    {
        var snapshot = Create(
            false,
            1,
            new StreamingProviderCapacitySnapshot(
                ProviderType.BackupAndStats, ProviderCircuitState.Closed, 20, null));

        Assert.Equal(StreamingCapacityReason.NoHealthyPrimaryCapacity, snapshot.Reason);
        Assert.Equal(1, snapshot.EffectiveStreamConnectionTarget);
    }

    private static StreamingCapacitySnapshot Create(
        bool isPerStream,
        int readers,
        params StreamingProviderCapacitySnapshot[] providers) =>
        StreamingCapacitySnapshotProvider.CreateSnapshot(
            isPerStream,
            configuredDownloadBudget: 20,
            configuredPerStreamBudget: 15,
            readerCount: readers,
            articleBufferSize: 40,
            inFlightArticleBudgetBytes: 64 * 1024 * 1024,
            providers: providers);

    private static StreamingProviderCapacitySnapshot Provider(
        int effectiveMax,
        int? transferLimit = null) =>
        new(ProviderType.Pooled, ProviderCircuitState.Closed, effectiveMax, transferLimit);
}
