using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// The sweep exists because a FUSE mount outlives the process that made it, so a
/// killed container leaves one behind and the next start cannot mount over it.
/// The risk it has to avoid is the opposite mistake: tearing down a mount that
/// is still serving somebody's library.
/// </summary>
public class RcloneStaleMountCleanerTests
{
    private static RcloneStaleMountCleaner Cleaner(
        IReadOnlyList<MountTableEntry> table,
        Func<string, bool>? responsive = null,
        List<string>? unmounted = null,
        bool unmountSucceeds = true) =>
        new()
        {
            ReadMountTable = () => table,
            IsResponsive = responsive ?? (_ => false),
            Unmount = path =>
            {
                unmounted?.Add(path);
                return unmountSucceeds;
            },
        };

    [Fact]
    public void Sweep_ClearsADeadMountOnOurOwnMountPoint()
    {
        var unmounted = new List<string>();
        var cleaner = Cleaner(
            [new MountTableEntry("/data/nzbdav", "fuse.rclone")],
            responsive: _ => false,
            unmounted: unmounted);

        var sweep = cleaner.Sweep(["/data/nzbdav"]);

        Assert.Equal(["/data/nzbdav"], unmounted);
        Assert.Equal(["/data/nzbdav"], sweep.Cleared);
        Assert.Empty(sweep.LeftAlone);
    }

    [Fact]
    public void Sweep_LeavesAMountThatStillAnswers()
    {
        // This is the external rclone the operator has not stopped yet. Unmounting
        // it would take their library offline mid-playback.
        var unmounted = new List<string>();
        var cleaner = Cleaner(
            [new MountTableEntry("/data/nzbdav", "fuse.rclone")],
            responsive: _ => true,
            unmounted: unmounted);

        var sweep = cleaner.Sweep(["/data/nzbdav"]);

        Assert.Empty(unmounted);
        Assert.Equal(["/data/nzbdav"], sweep.LeftAlone);
    }

    [Fact]
    public void Sweep_IgnoresPathsThatAreNotMountedAtAll()
    {
        var unmounted = new List<string>();
        var cleaner = Cleaner([], unmounted: unmounted);

        var sweep = cleaner.Sweep(["/data/nzbdav"]);

        Assert.Empty(unmounted);
        Assert.Empty(sweep.Cleared);
        Assert.Empty(sweep.LeftAlone);
    }

    [Fact]
    public void Sweep_NeverTouchesANonFuseFilesystem()
    {
        // A bind mount or a real disk mounted at the configured path is not
        // something to detach, however unresponsive it looks.
        var unmounted = new List<string>();
        var cleaner = Cleaner(
            [new MountTableEntry("/data/nzbdav", "ext4")],
            responsive: _ => false,
            unmounted: unmounted);

        var sweep = cleaner.Sweep(["/data/nzbdav"]);

        Assert.Empty(unmounted);
        Assert.Empty(sweep.Cleared);
    }

    [Fact]
    public void Sweep_NeverTouchesAPathWeDidNotConfigure()
    {
        // Someone else's dead FUSE mount is still not ours to remove.
        var unmounted = new List<string>();
        var cleaner = Cleaner(
            [new MountTableEntry("/mnt/someone-else", "fuse.rclone")],
            responsive: _ => false,
            unmounted: unmounted);

        cleaner.Sweep(["/data/nzbdav"]);

        Assert.Empty(unmounted);
    }

    [Fact]
    public void Sweep_ReportsAStaleMountItCouldNotRelease()
    {
        var cleaner = Cleaner(
            [new MountTableEntry("/data/nzbdav", "fuse.rclone")],
            responsive: _ => false,
            unmountSucceeds: false);

        var sweep = cleaner.Sweep(["/data/nzbdav"]);

        Assert.Equal(["/data/nzbdav"], sweep.Failed);
        Assert.Empty(sweep.Cleared);
    }

    [Fact]
    public void Sweep_MatchesMountPointsThatDifferOnlyByTrailingSlash()
    {
        var unmounted = new List<string>();
        var cleaner = Cleaner(
            [new MountTableEntry("/data/nzbdav/", "fuse.rclone")],
            unmounted: unmounted);

        cleaner.Sweep(["/data/nzbdav"]);

        Assert.Equal(["/data/nzbdav"], unmounted);
    }

    [Fact]
    public void Sweep_DoesNothingWhenTheMountTableCannotBeRead()
    {
        // Without the table there is no way to tell stale from live.
        var unmounted = new List<string>();
        var cleaner = new RcloneStaleMountCleaner
        {
            ReadMountTable = () => throw new IOException("no /proc"),
            IsResponsive = _ => false,
            Unmount = path =>
            {
                unmounted.Add(path);
                return true;
            },
        };

        var sweep = cleaner.Sweep(["/data/nzbdav"]);

        Assert.Empty(unmounted);
        Assert.Empty(sweep.Cleared);
    }

    [Fact]
    public void IsResponsiveWithin_TreatsAProbeThatNeverAnswersAsStillAlive()
    {
        // A mount whose daemon has gone fails instantly with ENOTCONN, which is
        // the case the sweep is for. A mount whose daemon is alive but wedged
        // never answers at all, and this probe runs on the supervisor's own loop.
        // Detaching something that may still be serving files is the worse
        // mistake, so no answer within the budget means leave it alone.
        using var blocked = new ManualResetEventSlim(false);
        try
        {
            var responsive = RcloneStaleMountCleaner.IsResponsiveWithin(
                () =>
                {
                    blocked.Wait();
                    return false;
                },
                TimeSpan.FromMilliseconds(100));

            Assert.True(responsive);
        }
        finally
        {
            blocked.Set();
        }
    }

    [Fact]
    public void IsResponsiveWithin_ReportsWhatAProbeThatAnswersSaid()
    {
        Assert.False(RcloneStaleMountCleaner.IsResponsiveWithin(() => false, TimeSpan.FromSeconds(5)));
        Assert.True(RcloneStaleMountCleaner.IsResponsiveWithin(() => true, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ParseMountInfo_ReadsTheMountPointAndFilesystemType()
    {
        // Real lines: the optional fields before " - " vary in number, which is
        // why the type is read after the separator rather than by position.
        var entries = RcloneStaleMountCleaner.ParseMountInfo(
        [
            "36 25 0:32 / /data/nzbdav rw,nosuid,nodev,relatime shared:18 - fuse.rclone rclone rw,user_id=1000",
            "24 30 0:22 / /sys rw,relatime - sysfs sysfs rw",
        ]);

        Assert.Collection(
            entries,
            first =>
            {
                Assert.Equal("/data/nzbdav", first.MountPoint);
                Assert.Equal("fuse.rclone", first.FilesystemType);
            },
            second =>
            {
                Assert.Equal("/sys", second.MountPoint);
                Assert.Equal("sysfs", second.FilesystemType);
            });
    }

    [Fact]
    public void ParseMountInfo_DecodesEscapedPaths()
    {
        var entries = RcloneStaleMountCleaner.ParseMountInfo(
            ["36 25 0:32 / /data/my\\040media rw,relatime - fuse.rclone rclone rw"]);

        Assert.Equal("/data/my media", Assert.Single(entries).MountPoint);
    }

    [Fact]
    public void ParseMountInfo_SkipsLinesItCannotRead()
    {
        var entries = RcloneStaleMountCleaner.ParseMountInfo(["garbage", "", "36 25 0:32 /"]);

        Assert.Empty(entries);
    }
}
