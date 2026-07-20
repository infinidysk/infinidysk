using NzbWebDAV.Api.Controllers.UsenetMigration;

namespace NzbWebDAV.Tests.UsenetMigration;

public class MigrationRunStateTests
{
    [Theory]
    [InlineData("scanned", true)]
    [InlineData("running", false)]
    [InlineData("paused", false)]
    [InlineData("complete", false)]
    [InlineData("cancelled", false)]
    [InlineData("scanning", false)]
    public void StartMigration_RequiresFreshCompletedScan(string status, bool expected) =>
        Assert.Equal(expected, UsenetMigrationController.CanStartMigration(status));

    [Theory]
    [InlineData("running", false)]
    [InlineData("paused", false)]
    [InlineData("complete", true)]
    [InlineData("cancelled", true)]
    [InlineData("scanned", true)]
    public void StartScan_IsBlockedOnlyByActiveRun(string status, bool expected) =>
        Assert.Equal(expected, UsenetMigrationController.CanStartScan(status));

    [Theory]
    [InlineData("paused", true)]
    [InlineData("scanned", false)]
    [InlineData("running", false)]
    [InlineData("complete", false)]
    [InlineData("cancelled", false)]
    public void ResumeMigration_RequiresPausedRun(string status, bool expected) =>
        Assert.Equal(expected, UsenetMigrationController.CanResumeMigration(status));

    [Fact]
    public void PauseAndCancel_ExposeOnlyValidActiveTransitions()
    {
        Assert.True(UsenetMigrationController.CanPauseMigration("running"));
        Assert.False(UsenetMigrationController.CanPauseMigration("paused"));
        Assert.True(UsenetMigrationController.CanCancelMigration("running"));
        Assert.True(UsenetMigrationController.CanCancelMigration("paused"));
        Assert.False(UsenetMigrationController.CanCancelMigration("scanned"));
        Assert.False(UsenetMigrationController.CanCancelMigration("complete"));
    }
}
