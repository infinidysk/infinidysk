using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class UrgentRepairDispositionTests
{
    [Theory]
    [InlineData(0, 0, true, HealthCheckService.UrgentRepairDisposition.RepairNormally)]
    [InlineData(0, 5, true, HealthCheckService.UrgentRepairDisposition.RepairNormally)]
    [InlineData(0, 5, false, HealthCheckService.UrgentRepairDisposition.RepairNormally)]
    public void ThresholdZero_AlwaysRepairNormally(
        int threshold,
        int failureCount,
        bool unlinkedOnly,
        HealthCheckService.UrgentRepairDisposition expected)
    {
        Assert.Equal(
            expected,
            HealthCheckService.GetUrgentRepairDisposition(threshold, failureCount, unlinkedOnly));
    }

    [Fact]
    public void Unlinked_BelowThreshold_Defers()
    {
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.Defer,
            HealthCheckService.GetUrgentRepairDisposition(3, 2, autoRemoveUnlinkedOnly: true));
    }

    [Fact]
    public void UnlinkedOnly_AtThreshold_DefersLinkDecisionUntilRepair()
    {
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.ForceDeleteIfUnlinked,
            HealthCheckService.GetUrgentRepairDisposition(3, 3, autoRemoveUnlinkedOnly: true));
    }

    [Fact]
    public void UnlinkedOnly_DefersUntilThresholdThenDefersLinkDecision()
    {
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.Defer,
            HealthCheckService.GetUrgentRepairDisposition(3, 1, autoRemoveUnlinkedOnly: true));
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.ForceDeleteIfUnlinked,
            HealthCheckService.GetUrgentRepairDisposition(3, 3, autoRemoveUnlinkedOnly: true));
    }

    [Fact]
    public void Linked_Aggressive_BelowThreshold_Defers_AtThreshold_ForceDeletes()
    {
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.Defer,
            HealthCheckService.GetUrgentRepairDisposition(3, 2, autoRemoveUnlinkedOnly: false));
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.ForceDelete,
            HealthCheckService.GetUrgentRepairDisposition(3, 3, autoRemoveUnlinkedOnly: false));
    }

    [Fact]
    public void MissingLibraryLink_WithoutForceDelete_Defers()
    {
        Assert.Equal(
            HealthCheckService.LibraryLinkRepairDisposition.DeferMissingLink,
            HealthCheckService.GetLibraryLinkRepairDisposition(null, forceDelete: false));
    }

    [Fact]
    public void MissingLibraryLink_WithExplicitForceDelete_Deletes()
    {
        Assert.Equal(
            HealthCheckService.LibraryLinkRepairDisposition.ForceDelete,
            HealthCheckService.GetLibraryLinkRepairDisposition(null, forceDelete: true));
    }

    [Fact]
    public void PresentLibraryLink_UsesArrRepair()
    {
        Assert.Equal(
            HealthCheckService.LibraryLinkRepairDisposition.RepairLinked,
            HealthCheckService.GetLibraryLinkRepairDisposition("/library/movie.mkv", forceDelete: false));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void ArrNoMatch_RequiresRepeatedConfirmation(int count, bool expected)
    {
        Assert.Equal(expected, HealthCheckService.ShouldDeleteAfterArrNoMatch(count));
    }
}
