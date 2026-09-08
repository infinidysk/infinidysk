using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RcloneMountsStatusBuilderTests
{
    private static RcloneMountConfig Configured(string id = "library", string mountPoint = "/mnt/remote") =>
        new() { Id = id, MountPoint = mountPoint };

    private static RcloneDaemonStatus Running() =>
        new(Enabled: true, Running: true, BaseUrl: "http://127.0.0.1:5572", BlockingReasons: []);

    [Fact]
    public void Build_MarksAConfiguredMountAsMountedWhenItIsLive()
    {
        var status = RcloneMountsStatusBuilder.Build(
            Running(),
            [Configured()],
            [new RcloneMountPoint { Fs = "infinidysk:", MountPoint = "/mnt/remote" }]);

        var mount = Assert.Single(status.Mounts);
        Assert.Equal("library", mount.Id);
        Assert.True(mount.Mounted);
    }

    [Fact]
    public void Build_MarksAConfiguredMountAsNotMountedWhenItIsAbsent()
    {
        var status = RcloneMountsStatusBuilder.Build(Running(), [Configured()], []);

        Assert.False(Assert.Single(status.Mounts).Mounted);
    }

    [Fact]
    public void Build_ReportsAMountThatIsLiveButNoLongerConfigured()
    {
        var status = RcloneMountsStatusBuilder.Build(
            Running(),
            [],
            [new RcloneMountPoint { Fs = "infinidysk:", MountPoint = "/mnt/orphan" }]);

        var mount = Assert.Single(status.Mounts);
        Assert.Equal("/mnt/orphan", mount.MountPoint);
        Assert.True(mount.Mounted);
        Assert.False(mount.Configured);
    }

    [Fact]
    public void Build_ReportsNothingAsMountedWhenTheDaemonIsNotRunning()
    {
        var stopped = new RcloneDaemonStatus(
            Enabled: true,
            Running: false,
            BaseUrl: null,
            BlockingReasons: ["/dev/fuse is not available inside the container."]);

        // A stale listing must not survive the daemon going away, so the live
        // list deliberately still contains the configured mount point.
        var stale = new List<RcloneMountPoint>
        {
            new() { Fs = "infinidysk:/", MountPoint = Configured().MountPoint },
        };

        var status = RcloneMountsStatusBuilder.Build(stopped, [Configured()], stale);

        Assert.False(status.Running);
        Assert.False(Assert.Single(status.Mounts).Mounted);
        Assert.NotEmpty(status.BlockingReasons);
    }

    // Without a remote the daemon can run but can never mount, so the UI has to
    // be able to ask for the WebDAV password instead of showing a stuck mount.
    [Fact]
    public void Build_ReportsWhenTheWebdavRemoteHasNotBeenCreatedYet()
    {
        var status = RcloneMountsStatusBuilder.Build(Running(), [Configured()], [], remoteConfigured: false);

        Assert.False(status.RemoteConfigured);
    }

    [Fact]
    public void Build_ReportsAConfiguredRemote()
    {
        var status = RcloneMountsStatusBuilder.Build(Running(), [Configured()], [], remoteConfigured: true);

        Assert.True(status.RemoteConfigured);
    }

    [Fact]
    public void Build_CarriesTheDisabledFlagThroughSoTheUiCanShowIt()
    {
        var disabled = Configured();
        disabled.Enabled = false;

        var status = RcloneMountsStatusBuilder.Build(Running(), [disabled], []);

        Assert.False(Assert.Single(status.Mounts).Enabled);
    }
}
