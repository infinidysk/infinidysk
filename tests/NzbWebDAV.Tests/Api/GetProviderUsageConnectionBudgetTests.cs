using NzbWebDAV.Api.Controllers.GetProviderUsage;
using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Tests.Api;

public class GetProviderUsageConnectionBudgetTests
{
    [Fact]
    public void ToBudgetItem_ReturnsNullForLegacyScheduling()
    {
        Assert.Null(GetProviderUsageController.ToBudgetItem(null));
    }

    [Fact]
    public void ToBudgetItem_ProjectsEveryRuntimeAdmissionField()
    {
        var snapshot = new ProviderConnectionAdmissionSnapshot(
            ConfiguredTransferLimit: 20,
            EffectiveTransferLimit: 15,
            BaseMetadataCapacity: 0,
            MetadataBurstAllowance: 7,
            MaxMetadataCapacity: 7,
            ActiveTransferOperations: 10,
            ActiveMetadataOperations: 5,
            WaitingTransferOperations: 2,
            WaitingMetadataOperations: 3);

        var item = Assert.IsType<GetProviderUsageResponse.ProviderConnectionBudgetItem>(
            GetProviderUsageController.ToBudgetItem(snapshot));

        Assert.Equal(20, item.ConfiguredTransferLimit);
        Assert.Equal(15, item.EffectiveTransferLimit);
        Assert.Equal(0, item.BaseMetadataCapacity);
        Assert.Equal(7, item.MetadataBurstAllowance);
        Assert.Equal(7, item.MaxMetadataCapacity);
        Assert.Equal(10, item.ActiveTransferOperations);
        Assert.Equal(5, item.ActiveMetadataOperations);
        Assert.Equal(2, item.WaitingTransferOperations);
        Assert.Equal(3, item.WaitingMetadataOperations);
    }
}
