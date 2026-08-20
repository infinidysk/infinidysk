using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class MigrationProgressTests
{
    [Theory]
    [InlineData(0, 0, false, true)]
    [InlineData(0, 0, true, false)]
    [InlineData(1, 0, false, false)]
    [InlineData(0, 1, false, false)]
    [InlineData(2, 3, true, false)]
    public void IsIdleMaintenance_MatchesPendingAndVacuum(
        int pendingCount,
        int pendingMetricsCount,
        bool vacuumEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            MigrationProgress.IsIdleMaintenance(
                pendingCount,
                pendingMetricsCount,
                vacuumEnabled));
    }

    [Fact]
    public void IsSlow_FlagsPathIndexMigration()
    {
        Assert.True(MigrationProgress.IsSlow("20260713120000_Add-Path-Index-To-DavItems"));
    }

    [Fact]
    public void IsSlow_FlagsGuidTextCasingMigration()
    {
        Assert.True(MigrationProgress.IsSlow("20260820160000_Normalize-Guid-Text-Casing"));
    }
}
