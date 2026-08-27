using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ProviderConnectionBudgetTests
{
    [Theory]
    [InlineData(50, 20, 50, 20, 30, 10, 40)]
    [InlineData(50, 50, 50, 50, 0, 25, 25)]
    [InlineData(40, 16, 40, 16, 24, 8, 32)]
    [InlineData(43, 20, 43, 20, 23, 10, 33)]
    [InlineData(15, 20, 15, 15, 0, 7, 7)]
    [InlineData(10, 4, 10, 4, 6, 2, 8)]
    [InlineData(50, 21, 50, 21, 29, 10, 39)]
    public void Calculate_DerivesEffectiveLimits(
        int effectiveProviderLimit,
        int configuredTransferLimit,
        int expectedProviderLimit,
        int expectedTransferLimit,
        int expectedBaseMetadata,
        int expectedBurst,
        int expectedMetadataMax)
    {
        var budget = ProviderConnectionBudget.Calculate(
            effectiveProviderLimit,
            configuredTransferLimit);

        Assert.Equal(expectedProviderLimit, budget.EffectiveProviderLimit);
        Assert.Equal(expectedTransferLimit, budget.EffectiveTransferLimit);
        Assert.Equal(expectedBaseMetadata, budget.BaseMetadataCapacity);
        Assert.Equal(expectedBurst, budget.MetadataBurstAllowance);
        Assert.Equal(expectedMetadataMax, budget.MaxMetadataCapacity);
    }

    [Fact]
    public void Calculate_OneConnectionProviderAllowsOpportunisticMetadata()
    {
        var budget = ProviderConnectionBudget.Calculate(1, 1);

        Assert.Equal(1, budget.EffectiveProviderLimit);
        Assert.Equal(1, budget.EffectiveTransferLimit);
        Assert.Equal(0, budget.BaseMetadataCapacity);
        Assert.Equal(1, budget.MetadataBurstAllowance);
        Assert.Equal(1, budget.MaxMetadataCapacity);
    }

    [Fact]
    public void Calculate_NormalizesNonPositiveRuntimeProviderLimit()
    {
        var budget = ProviderConnectionBudget.Calculate(0, 20);

        Assert.Equal(1, budget.EffectiveProviderLimit);
        Assert.Equal(1, budget.EffectiveTransferLimit);
        Assert.Equal(1, budget.MaxMetadataCapacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_RejectsNonPositiveConfiguredTransferLimit(int configuredTransferLimit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProviderConnectionBudget.Calculate(10, configuredTransferLimit));
    }
}
