using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RcloneImportTranslatorTests
{
    private static RcloneMountPoint LiveMount(string mountPoint = "/mnt/remote/nzbdav", string fs = "nzbdav:") =>
        new() { Fs = fs, MountPoint = mountPoint };

    // Values as rclone actually reports them: durations in nanoseconds, sizes in
    // bytes, -1 meaning unlimited. Captured from rclone v1.75.1 vfs/stats.
    private static VfsOptions TunedOptions() => new()
    {
        CacheMode = "full",
        DirCacheTime = 20_000_000_000,
        CacheMaxAge = 86_400_000_000_000,
        CacheMaxSize = 21_474_836_480,
        ReadAhead = 536_870_912,
        Links = true,
    };

    [Fact]
    public void Translate_KeepsTheExternalMountPointExactly()
    {
        var mount = RcloneImportTranslator.Translate(LiveMount(), TunedOptions());

        // Existing Sonarr/Radarr symlinks resolve through this path. Changing it
        // during an import silently breaks every imported file.
        Assert.Equal("/mnt/remote/nzbdav", mount.MountPoint);
    }

    [Fact]
    public void Translate_CarriesTheCacheModeOver()
    {
        var mount = RcloneImportTranslator.Translate(LiveMount(), TunedOptions());

        Assert.Equal(RcloneVfsCacheMode.Full, mount.VfsCacheMode);
    }

    [Fact]
    public void Translate_ConvertsNanosecondDurationsToTimeSpans()
    {
        var mount = RcloneImportTranslator.Translate(LiveMount(), TunedOptions());

        Assert.Equal(TimeSpan.FromSeconds(20), mount.DirCacheTime);
        Assert.Equal(TimeSpan.FromHours(24), mount.VfsCacheMaxAge);
    }

    [Fact]
    public void Translate_CarriesSizesInBytes()
    {
        var mount = RcloneImportTranslator.Translate(LiveMount(), TunedOptions());

        Assert.Equal(21_474_836_480, mount.VfsCacheMaxSizeBytes);
        Assert.Equal(536_870_912, mount.ReadAheadBytes);
    }

    [Fact]
    public void Translate_TreatsMinusOneAsUnlimited()
    {
        var options = TunedOptions();
        options.CacheMaxSize = -1;

        var mount = RcloneImportTranslator.Translate(LiveMount(), options);

        Assert.Null(mount.VfsCacheMaxSizeBytes);
    }

    [Fact]
    public void Translate_PreservesLinksBecauseSymlinkImportsDependOnIt()
    {
        var mount = RcloneImportTranslator.Translate(LiveMount(), TunedOptions());

        Assert.True(mount.Links);
    }

    [Theory]
    // rclone reports a root mount as "nzbdav:" even when it was mounted as "nzbdav:/".
    [InlineData("nzbdav:", "/")]
    [InlineData("nzbdav:/", "/")]
    [InlineData("nzbdav:/content", "/content")]
    public void Translate_RecoversTheRemotePathFromTheFsString(string fs, string expected)
    {
        var mount = RcloneImportTranslator.Translate(LiveMount(fs: fs), TunedOptions());

        Assert.Equal(expected, mount.RemotePath);
    }

    [Fact]
    public void Translate_DerivesAStableIdFromTheMountPoint()
    {
        var first = RcloneImportTranslator.Translate(LiveMount(), TunedOptions());
        var second = RcloneImportTranslator.Translate(LiveMount(), TunedOptions());

        Assert.Equal(first.Id, second.Id);
        Assert.NotEmpty(first.Id);
    }

    [Fact]
    public void Translate_ProducesAMountThatPassesValidation()
    {
        var mount = RcloneImportTranslator.Translate(LiveMount(), TunedOptions());

        Assert.Empty(RcloneMountConfig.Validate([mount], "/config"));
    }

    [Fact]
    public void Translate_FallsBackToDefaults_WhenTheDaemonReportsNoOptions()
    {
        var mount = RcloneImportTranslator.Translate(LiveMount(), null);

        Assert.Equal(RcloneVfsCacheMode.Full, mount.VfsCacheMode);
        Assert.Equal("/mnt/remote/nzbdav", mount.MountPoint);
    }

    [Fact]
    public void Translate_KeepsCachingSwitchedOff_WhenTheLiveMountHasItOff()
    {
        // --dir-cache-time=0 --vfs-cache-max-age=0 is how an operator turns
        // caching off. Importing those as "unset" replaces them with the
        // seven-day defaults, which is the opposite of the mount being imported.
        var mount = RcloneImportTranslator.Translate(
            new RcloneMountPoint { MountPoint = "/mnt/remote", Fs = "nzbdav:" },
            new VfsOptions { CacheMode = "full", DirCacheTime = 0, CacheMaxAge = 0 });

        Assert.Equal(TimeSpan.Zero, mount.DirCacheTime);
        Assert.Equal(TimeSpan.Zero, mount.VfsCacheMaxAge);
    }

    [Fact]
    public void Translate_KeepsReadAheadSwitchedOff_WhenTheLiveMountHasItOff()
    {
        var mount = RcloneImportTranslator.Translate(
            new RcloneMountPoint { MountPoint = "/mnt/remote", Fs = "nzbdav:" },
            new VfsOptions { CacheMode = "full", ReadAhead = 0 });

        Assert.Equal(0, mount.ReadAheadBytes);
    }

    [Fact]
    public void Translate_TreatsRcloneNoLimitAsUnset()
    {
        var mount = RcloneImportTranslator.Translate(
            new RcloneMountPoint { MountPoint = "/mnt/remote", Fs = "nzbdav:" },
            new VfsOptions { CacheMode = "full", CacheMaxSize = -1, DirCacheTime = -1 });

        Assert.Null(mount.VfsCacheMaxSizeBytes);
        Assert.Equal(TimeSpan.FromDays(7), mount.DirCacheTime);
    }

    [Fact]
    public void Translate_RecordsAZeroCacheCeilingAsUnset()
    {
        // Nothing downstream can apply a zero-byte cache: the budget and the
        // status endpoint both read zero as "no ceiling set". Storing it would
        // record a limit that never takes effect.
        var mount = RcloneImportTranslator.Translate(
            new RcloneMountPoint { MountPoint = "/mnt/remote", Fs = "nzbdav:" },
            new VfsOptions { CacheMode = "full", CacheMaxSize = 0 });

        Assert.Null(mount.VfsCacheMaxSizeBytes);
    }
}
