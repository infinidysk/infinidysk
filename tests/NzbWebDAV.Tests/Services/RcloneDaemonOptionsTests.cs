using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RcloneDaemonOptionsTests
{
    private static RcloneDaemonOptions Options() => new()
    {
        RcPort = 5572,
        ConfigFilePath = "/config/rclone/rclone.conf",
        CacheDir = "/config/rclone/cache",
        RcUser = "infinidysk",
        RcPass = "s3cret",
    };

    [Fact]
    public void ToArguments_StartsTheRemoteControlDaemonBoundToLoopback()
    {
        var arguments = Options().ToArguments();

        Assert.Equal("rcd", arguments[0]);
        Assert.Contains("--rc-addr", arguments);
        Assert.Contains("127.0.0.1:5572", arguments);
    }

    [Fact]
    public void ToArguments_PassesThePaths()
    {
        var arguments = Options().ToArguments();

        Assert.Contains("/config/rclone/rclone.conf", arguments);
        Assert.Contains("/config/rclone/cache", arguments);
    }

    [Fact]
    public void ToArguments_TurnsOffThePerTransferMemoryBuffer()
    {
        // rclone buffers 16 MiB in memory per transfer by default, which
        // multiplies across concurrent streams for no benefit once read-ahead is
        // buffering on disk instead.
        var arguments = Options().ToArguments();

        Assert.Contains("--buffer-size=0", arguments);
    }

    [Fact]
    public void ToArguments_EnablesTheSessionCookieJar()
    {
        var arguments = Options().ToArguments();

        Assert.Contains("--use-cookies", arguments);
    }

    [Fact]
    public void ToArguments_KeepsCredentialsOffTheCommandLine()
    {
        // Anything else in the container can read argv through /proc, so the RC
        // credentials must not appear there.
        var arguments = Options().ToArguments();

        Assert.DoesNotContain("--rc-user", arguments);
        Assert.DoesNotContain("--rc-pass", arguments);
        Assert.DoesNotContain("s3cret", arguments);
    }

    [Fact]
    public void ToEnvironment_CarriesTheCredentialsRcloneReads()
    {
        var environment = Options().ToEnvironment();

        Assert.Equal("infinidysk", environment["RCLONE_RC_USER"]);
        Assert.Equal("s3cret", environment["RCLONE_RC_PASS"]);
    }

    [Fact]
    public void HasSameSettingsAs_IgnoresTheGeneratedPassword()
    {
        // The password is regenerated on every start, so comparing it would
        // restart a healthy daemon on every supervisor pass.
        var running = Options();
        var rebuilt = new RcloneDaemonOptions
        {
            RcPort = 5572,
            ConfigFilePath = "/config/rclone/rclone.conf",
            CacheDir = "/config/rclone/cache",
            RcUser = "infinidysk",
            RcPass = "a-different-password",
        };

        Assert.True(rebuilt.HasSameSettingsAs(running));
    }

    [Fact]
    public void HasSameSettingsAs_DetectsAChangedCacheDirectory()
    {
        var running = Options();
        var rebuilt = new RcloneDaemonOptions
        {
            RcPort = 5572,
            ConfigFilePath = "/config/rclone/rclone.conf",
            CacheDir = "/mnt/fast/rclone-cache",
            RcUser = "infinidysk",
            RcPass = "s3cret",
        };

        Assert.False(rebuilt.HasSameSettingsAs(running));
    }

    [Fact]
    public void BaseUrl_PointsAtLoopback()
    {
        Assert.Equal("http://127.0.0.1:5572", Options().BaseUrl);
    }

    [Fact]
    public void GenerateCredentials_ProducesDistinctUnguessableSecrets()
    {
        var first = RcloneDaemonOptions.GenerateRcPassword();
        var second = RcloneDaemonOptions.GenerateRcPassword();

        Assert.NotEqual(first, second);
        Assert.True(first.Length >= 32, $"expected at least 32 characters, got {first.Length}");
    }

    [Fact]
    public void Describe_DoesNotLeakThePassword()
    {
        var described = Options().Describe();

        Assert.DoesNotContain("s3cret", described, StringComparison.Ordinal);
        Assert.Contains("rcd", described, StringComparison.Ordinal);
    }
}
