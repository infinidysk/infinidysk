using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class UrgentRepairDispositionTests
{
    [Theory]
    [InlineData(0, 0, false, true, HealthCheckService.UrgentRepairDisposition.RepairNormally)]
    [InlineData(0, 5, false, true, HealthCheckService.UrgentRepairDisposition.RepairNormally)]
    [InlineData(0, 5, true, true, HealthCheckService.UrgentRepairDisposition.RepairNormally)]
    public void ThresholdZero_AlwaysRepairNormally(
        int threshold,
        int failureCount,
        bool hasLibraryLink,
        bool unlinkedOnly,
        HealthCheckService.UrgentRepairDisposition expected)
    {
        Assert.Equal(
            expected,
            HealthCheckService.GetUrgentRepairDisposition(threshold, failureCount, hasLibraryLink, unlinkedOnly));
    }

    [Fact]
    public void Unlinked_BelowThreshold_Defers()
    {
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.Defer,
            HealthCheckService.GetUrgentRepairDisposition(3, 2, hasLibraryLink: false, autoRemoveUnlinkedOnly: true));
    }

    [Fact]
    public void Unlinked_AtThreshold_ForceDeletes()
    {
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.ForceDelete,
            HealthCheckService.GetUrgentRepairDisposition(3, 3, hasLibraryLink: false, autoRemoveUnlinkedOnly: true));
    }

    [Fact]
    public void Linked_UnlinkedOnly_DefersUntilThresholdThenUsesArrPath()
    {
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.Defer,
            HealthCheckService.GetUrgentRepairDisposition(3, 1, hasLibraryLink: true, autoRemoveUnlinkedOnly: true));
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.RepairNormally,
            HealthCheckService.GetUrgentRepairDisposition(3, 3, hasLibraryLink: true, autoRemoveUnlinkedOnly: true));
    }

    [Fact]
    public void Linked_Aggressive_BelowThreshold_Defers_AtThreshold_ForceDeletes()
    {
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.Defer,
            HealthCheckService.GetUrgentRepairDisposition(3, 2, hasLibraryLink: true, autoRemoveUnlinkedOnly: false));
        Assert.Equal(
            HealthCheckService.UrgentRepairDisposition.ForceDelete,
            HealthCheckService.GetUrgentRepairDisposition(3, 3, hasLibraryLink: true, autoRemoveUnlinkedOnly: false));
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
}
