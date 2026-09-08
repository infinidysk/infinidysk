using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RcloneCapabilityCheckTests
{
    private static RcloneCapabilityCheck Check(
        bool rcloneBinary = true,
        bool fusermount = true,
        bool fuseDevice = true,
        bool fuseDeviceWritable = true,
        string? fuseConf = "user_allow_other\n") =>
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
            ReadFileText = _ => fuseConf,
        };

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
