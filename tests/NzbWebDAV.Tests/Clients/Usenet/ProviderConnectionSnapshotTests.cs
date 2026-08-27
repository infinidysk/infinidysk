using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ProviderConnectionSnapshotTests
{
    [Fact]
    public void LegacyProviderSnapshotHasNoAdmissionState()
    {
        using var client = CreateMultiProvider(maxTransferConnections: null);

        var snapshot = Assert.Single(client.GetProviderConnectionSnapshots());

        Assert.Equal(4, snapshot.ConfiguredMaxConnections);
        Assert.Equal(4, snapshot.EffectiveMaxConnections);
        Assert.Null(snapshot.Admission);
    }

    [Fact]
    public void BudgetedProviderSnapshotIncludesEffectiveAdmissionLimits()
    {
        using var client = CreateMultiProvider(maxTransferConnections: 2);

        var snapshot = Assert.Single(client.GetProviderConnectionSnapshots());
        var admission = Assert.IsType<ProviderConnectionAdmissionSnapshot>(snapshot.Admission);

        Assert.Equal(4, snapshot.ConfiguredMaxConnections);
        Assert.Equal(4, snapshot.EffectiveMaxConnections);
        Assert.Equal(2, admission.ConfiguredTransferLimit);
        Assert.Equal(2, admission.EffectiveTransferLimit);
        Assert.Equal(2, admission.BaseMetadataCapacity);
        Assert.Equal(1, admission.MetadataBurstAllowance);
        Assert.Equal(3, admission.MaxMetadataCapacity);
    }

    private static MultiProviderNntpClient CreateMultiProvider(int? maxTransferConnections)
    {
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 4,
            _ => throw new InvalidOperationException("Snapshot tests do not acquire connections."));
        var provider = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("snapshot-test"),
            "snapshot-test",
            maxTransferConnections: maxTransferConnections);
        return new MultiProviderNntpClient([provider]);
    }
}
