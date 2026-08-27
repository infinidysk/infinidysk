using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using UsenetSharp.Concurrency;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ProviderConnectionAdmissionTests
{
    [Fact]
    public async Task TransfersNeverExceedHardCap()
    {
        using var admission = CreateAdmission(providerLimit: 5, transferLimit: 2);
        using var first = await AcquireTransfer(admission);
        using var second = await AcquireTransfer(admission);

        var waiting = AcquireTransfer(admission);

        Assert.False(waiting.IsCompleted);
        first.Dispose();
        using var third = await waiting.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task MetadataNeverExceedsBoundedBurst()
    {
        using var admission = CreateAdmission(providerLimit: 5, transferLimit: 2);
        var leases = new List<ProviderConnectionAdmission.Lease>();
        try
        {
            for (var i = 0; i < 4; i++)
                leases.Add(await AcquireMetadata(admission));

            var waiting = AcquireMetadata(admission);

            Assert.False(waiting.IsCompleted);
            leases[0].Dispose();
            using var replacement = await waiting.WaitAsync(TestTimeout);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    [Fact]
    public async Task CombinedUsageNeverExceedsProviderLimit()
    {
        using var admission = CreateAdmission(providerLimit: 5, transferLimit: 2);
        using var transfer1 = await AcquireTransfer(admission);
        using var transfer2 = await AcquireTransfer(admission);
        using var metadata1 = await AcquireMetadata(admission);
        using var metadata2 = await AcquireMetadata(admission);
        using var metadata3 = await AcquireMetadata(admission);

        var waitingMetadata = AcquireMetadata(admission);

        Assert.False(waitingMetadata.IsCompleted);
        metadata1.Dispose();
        using var metadata4 = await waitingMetadata.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task WaitingTransferReclaimsBorrowedMetadataCapacityFirst()
    {
        using var admission = CreateAdmission(providerLimit: 5, transferLimit: 2);
        using var transfer1 = await AcquireTransfer(admission);
        using var metadata1 = await AcquireMetadata(admission);
        using var metadata2 = await AcquireMetadata(admission);
        using var metadata3 = await AcquireMetadata(admission);
        using var metadata4 = await AcquireMetadata(admission);

        var waitingTransfer = AcquireTransfer(admission);
        var waitingMetadata = AcquireMetadata(admission);
        Assert.False(waitingTransfer.IsCompleted);
        Assert.False(waitingMetadata.IsCompleted);

        metadata1.Dispose();

        using var transfer2 = await waitingTransfer.WaitAsync(TestTimeout);
        Assert.False(waitingMetadata.IsCompleted);

        metadata2.Dispose();
        using var replacementMetadata = await waitingMetadata.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task OneConnectionProviderAllowsMetadataWhileTransferSlotIsIdle()
    {
        using var admission = CreateAdmission(providerLimit: 1, transferLimit: 1);
        using var metadata = await AcquireMetadata(admission);

        var waitingTransfer = AcquireTransfer(admission);

        Assert.False(waitingTransfer.IsCompleted);
        metadata.Dispose();
        using var transfer = await waitingTransfer.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task RuntimeProviderLimitShrinkBlocksNewAdmissionsUntilUsageDrains()
    {
        var providerLimit = 5;
        using var admission = new ProviderConnectionAdmission(
            () => Volatile.Read(ref providerLimit),
            configuredTransferLimit: 2);
        using var metadata1 = await AcquireMetadata(admission);
        using var metadata2 = await AcquireMetadata(admission);
        using var metadata3 = await AcquireMetadata(admission);
        using var metadata4 = await AcquireMetadata(admission);

        Volatile.Write(ref providerLimit, 3);
        var waitingTransfer = AcquireTransfer(admission);
        metadata1.Dispose();
        Assert.False(waitingTransfer.IsCompleted);

        metadata2.Dispose();
        using var transfer = await waitingTransfer.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task CanceledWaiterDoesNotConsumeCapacity()
    {
        using var admission = CreateAdmission(providerLimit: 1, transferLimit: 1);
        using var held = await AcquireTransfer(admission);
        using var cancellation = new CancellationTokenSource();
        var canceledWaiter = admission.AcquireAsync(
            ProviderConnectionKind.Transfer,
            SemaphorePriority.Low,
            cancellation.Token);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);

        var nextWaiter = AcquireTransfer(admission);
        held.Dispose();
        using var next = await nextWaiter.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ExistingHighPriorityOrderingIsPreservedWithinTransferWaiters()
    {
        using var admission = CreateAdmission(
            providerLimit: 1,
            transferLimit: 1,
            new SemaphorePriorityOdds { HighPriorityOdds = 100 });
        using var held = await AcquireTransfer(admission);
        var low = admission.AcquireAsync(
            ProviderConnectionKind.Transfer,
            SemaphorePriority.Low,
            CancellationToken.None);
        var high = admission.AcquireAsync(
            ProviderConnectionKind.Transfer,
            SemaphorePriority.High,
            CancellationToken.None);

        held.Dispose();

        using var highLease = await high.WaitAsync(TestTimeout);
        Assert.False(low.IsCompleted);
        highLease.Dispose();
        using var lowLease = await low.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task SnapshotReportsDerivedLimitsAndLiveOperationCounts()
    {
        using var admission = CreateAdmission(providerLimit: 5, transferLimit: 2);
        using var transfer1 = await AcquireTransfer(admission);
        using var metadata1 = await AcquireMetadata(admission);
        using var metadata2 = await AcquireMetadata(admission);
        using var metadata3 = await AcquireMetadata(admission);
        using var metadata4 = await AcquireMetadata(admission);
        var waitingTransfer = AcquireTransfer(admission);
        var waitingMetadata = AcquireMetadata(admission);

        var snapshot = admission.GetSnapshot();

        Assert.Equal(2, snapshot.ConfiguredTransferLimit);
        Assert.Equal(2, snapshot.EffectiveTransferLimit);
        Assert.Equal(3, snapshot.BaseMetadataCapacity);
        Assert.Equal(1, snapshot.MetadataBurstAllowance);
        Assert.Equal(4, snapshot.MaxMetadataCapacity);
        Assert.Equal(1, snapshot.ActiveTransferOperations);
        Assert.Equal(4, snapshot.ActiveMetadataOperations);
        Assert.Equal(1, snapshot.WaitingTransferOperations);
        Assert.Equal(1, snapshot.WaitingMetadataOperations);

        metadata1.Dispose();
        using var transfer2 = await waitingTransfer.WaitAsync(TestTimeout);
        metadata2.Dispose();
        using var replacementMetadata = await waitingMetadata.WaitAsync(TestTimeout);
    }

    [Fact]
    public void SnapshotUsesCurrentEffectiveProviderLimitWithoutChangingConfiguredTransferLimit()
    {
        var providerLimit = 43;
        using var admission = new ProviderConnectionAdmission(
            () => Volatile.Read(ref providerLimit),
            configuredTransferLimit: 20);

        var initial = admission.GetSnapshot();
        Volatile.Write(ref providerLimit, 15);
        var reduced = admission.GetSnapshot();

        Assert.Equal(20, initial.ConfiguredTransferLimit);
        Assert.Equal(20, initial.EffectiveTransferLimit);
        Assert.Equal(23, initial.BaseMetadataCapacity);
        Assert.Equal(33, initial.MaxMetadataCapacity);
        Assert.Equal(20, reduced.ConfiguredTransferLimit);
        Assert.Equal(15, reduced.EffectiveTransferLimit);
        Assert.Equal(0, reduced.BaseMetadataCapacity);
        Assert.Equal(7, reduced.MaxMetadataCapacity);
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static ProviderConnectionAdmission CreateAdmission(
        int providerLimit,
        int transferLimit,
        SemaphorePriorityOdds? priorityOdds = null) =>
        new(() => providerLimit, transferLimit, priorityOdds);

    private static Task<ProviderConnectionAdmission.Lease> AcquireTransfer(
        ProviderConnectionAdmission admission) =>
        admission.AcquireAsync(
            ProviderConnectionKind.Transfer,
            SemaphorePriority.Low,
            CancellationToken.None);

    private static Task<ProviderConnectionAdmission.Lease> AcquireMetadata(
        ProviderConnectionAdmission admission) =>
        admission.AcquireAsync(
            ProviderConnectionKind.Metadata,
            SemaphorePriority.Low,
            CancellationToken.None);
}
