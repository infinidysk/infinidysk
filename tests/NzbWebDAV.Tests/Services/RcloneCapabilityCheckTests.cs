using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RcloneCapabilityCheckTests
{
    private static RcloneCapabilityCheck Check(
        bool rcloneBinary = true,
        bool fusermount = true,
        bool fuseDevice = true,
        bool fuseDeviceWritable = true,
        string? fuseConf = "user_allow_other\n",
        string? processStatus = null) =>
        new()
        {
            FileExists = path => path switch
            {
                RcloneCapabilityCheck.RcloneBinaryPath => rcloneBinary,
                RcloneCapabilityCheck.FuseDevicePath => fuseDevice,
                _ => false,
            },
            ExecutableOnPath = _ => fusermount,
            CanOpenForWrite = _ => fuseDeviceWritable,
            ReadFileText = path => path == RcloneCapabilityCheck.ProcessStatusPath ? processStatus : fuseConf,
        };

    // Capability masks as read inside the image: CapBnd carries bit 21 only with
    // --cap-add SYS_ADMIN, and the PUID user's CapEff is empty either way.
    private const string WithCapAdd = "00000000a82425fb";
    private const string WithoutCapAdd = "00000000a80425fb";

    private static string ProcessStatus(int uid, string capEff, string capBnd) =>
        $"Name:\tNzbWebDAV\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nCapEff:\t{capEff}\nCapBnd:\t{capBnd}\n";

    [Fact]
    public void Evaluate_ReportsReady_WhenEverythingIsPresent()
    {
        var result = Check().Evaluate();

        Assert.True(result.CanMount);
        Assert.Empty(result.BlockingReasons);
    }

    [Fact]
    public void Evaluate_ExplainsHowToFixAMissingFuseDevice()
    {
        var result = Check(fuseDevice: false).Evaluate();

        Assert.False(result.CanMount);
        var reason = Assert.Single(result.BlockingReasons);
        Assert.Contains("--device /dev/fuse", reason, StringComparison.Ordinal);
        Assert.Contains("--cap-add SYS_ADMIN", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ReportsAFuseDeviceThatCannotBeOpened()
    {
        // The device node is passed in but the container lacks the privileges to
        // use it, which otherwise fails later as an opaque permission error.
        var result = Check(fuseDeviceWritable: false).Evaluate();

        Assert.False(result.CanMount);
        var reason = Assert.Single(result.BlockingReasons);
        Assert.Contains("cannot be opened for writing", reason, StringComparison.Ordinal);
        Assert.Contains("SYS_ADMIN", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ReportsMissingSysAdmin_ForTheAppUser()
    {
        // The device opens without SYS_ADMIN; it is the mount that fails, with a
        // permission error that reads like a bug.
        var result = Check(processStatus: ProcessStatus(1000, "0000000000000000", WithoutCapAdd)).Evaluate();

        Assert.False(result.CanMount);
        var reason = Assert.Single(result.BlockingReasons);
        Assert.Contains("CAP_SYS_ADMIN", reason, StringComparison.Ordinal);
        Assert.Contains("--cap-add SYS_ADMIN", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ReportsReady_ForTheAppUser_WhenTheContainerHasSysAdmin()
    {
        // The PUID user's effective set is empty even with --cap-add SYS_ADMIN.
        // rclone mounts through the setuid fusermount3, which gains the bounding
        // set, so reading the effective set here would block every install.
        var result = Check(processStatus: ProcessStatus(1000, "0000000000000000", WithCapAdd)).Evaluate();

        Assert.True(result.CanMount);
        Assert.Empty(result.BlockingReasons);
    }

    [Fact]
    public void Evaluate_JudgesRootByItsEffectiveSet()
    {
        var result = Check(processStatus: ProcessStatus(0, WithoutCapAdd, WithCapAdd)).Evaluate();

        Assert.False(result.CanMount);
        Assert.Contains(result.BlockingReasons, r => r.Contains("CAP_SYS_ADMIN", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_DoesNotBlock_WhenTheCapabilitySetsCannotBeRead()
    {
        // Off Linux there is no status file to read. Guessing "missing" would
        // refuse a mount nothing is known to prevent.
        Assert.True(Check(processStatus: null).Evaluate().CanMount);
    }

    [Fact]
    public void Evaluate_ReportsAMissingRcloneBinary()
    {
        var result = Check(rcloneBinary: false).Evaluate();

        Assert.False(result.CanMount);
        Assert.Contains(result.BlockingReasons, r => r.Contains(RcloneCapabilityCheck.RcloneBinaryPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ReportsAMissingUnmountHelper()
    {
        var result = Check(fusermount: false).Evaluate();

        Assert.False(result.CanMount);
        Assert.Contains(result.BlockingReasons, r => r.Contains("fusermount3", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ReportsFuseConfWithoutAllowOther()
    {
        // Every mount is created with --allow-other so media servers can read it;
        // without this line libfuse refuses the option and every mount fails.
        var result = Check(fuseConf: "# user_allow_other\n").Evaluate();

        Assert.False(result.CanMount);
        Assert.Contains(result.BlockingReasons, r => r.Contains("user_allow_other", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ReportsAnUnreadableFuseConf()
    {
        var result = Check(fuseConf: null).Evaluate();

        Assert.False(result.CanMount);
        Assert.Contains(result.BlockingReasons, r => r.Contains(RcloneCapabilityCheck.FuseConfPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_AcceptsFuseConfWithSurroundingContent()
    {
        var result = Check(fuseConf: "# /etc/fuse.conf\n\nmount_max = 1000\nuser_allow_other\n").Evaluate();

        Assert.True(result.CanMount);
    }

    [Fact]
    public void Evaluate_ReportsEveryProblemAtOnce()
    {
        var result = Check(
            rcloneBinary: false,
            fusermount: false,
            fuseDevice: false,
            fuseConf: null).Evaluate();

        Assert.Equal(4, result.BlockingReasons.Count);
    }
}
