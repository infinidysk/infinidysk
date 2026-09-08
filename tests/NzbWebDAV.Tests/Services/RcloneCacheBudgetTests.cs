using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RcloneCacheBudgetTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private static RcloneCacheBudget Budget(long? freeBytes, string cacheVolume = "/", string dbVolume = "/") =>
        new()
        {
            AvailableFreeSpace = _ => freeBytes,
            MountPointOf = path => path.StartsWith("/config", StringComparison.Ordinal) ? dbVolume : cacheVolume,
        };

    [Fact]
    public void ResolveFor_HonoursAnExplicitLimit()
    {
        // Someone who typed a number has thought about it; the automation exists
        // for everyone who has not.
        var resolved = Budget(500 * Gib).ResolveFor("/cache", "/config", 3 * Gib);

        Assert.Equal(3 * Gib, resolved);
    }

    [Fact]
    public void ResolveFor_CapsAtTheDocumentedDefault_WhenSpaceIsPlentiful()
    {
        var resolved = Budget(4000 * Gib, cacheVolume: "/mnt/fast").ResolveFor("/mnt/fast", "/config", null);

        Assert.Equal(RcloneCacheBudget.DefaultCapBytes, resolved);
    }

    [Fact]
    public void ResolveFor_TakesHalfOfFreeSpace_WhenTheVolumeIsSmall()
    {
        var resolved = Budget(10 * Gib, cacheVolume: "/mnt/small").ResolveFor("/mnt/small", "/config", null);

        Assert.Equal(5 * Gib, resolved);
    }

    [Fact]
    public void ResolveFor_LeavesHeadroomWhenTheCacheSharesTheDatabaseVolume()
    {
        // Filling the volume the databases live on is a far worse outcome than a
        // cache miss, and the default cache directory sits beside them.
        var resolved = Budget(8 * Gib).ResolveFor("/config/rclone/cache", "/config", null);

        Assert.Equal(8 * Gib - RcloneCacheBudget.DatabaseHeadroomBytes, resolved);
    }

    [Fact]
    public void ResolveFor_DoesNotApplyDatabaseHeadroomToASeparateVolume()
    {
        var resolved = Budget(8 * Gib, cacheVolume: "/mnt/fast", dbVolume: "/")
            .ResolveFor("/mnt/fast/cache", "/config", null);

        Assert.Equal(4 * Gib, resolved);
    }

    [Fact]
    public void Resolve_NeverGoesBelowAUsableFloor()
    {
        var resolved = RcloneCacheBudget.Resolve(1 * Gib, sharesDatabaseVolume: true, configuredBytes: null);

        Assert.Equal(RcloneCacheBudget.FloorBytes, resolved);
    }

    [Fact]
    public void Resolve_FallsBackToTheDefault_WhenFreeSpaceCannotBeRead()
    {
        // Unknown is not a reason to fall back to rclone's unlimited default.
        var resolved = RcloneCacheBudget.Resolve(null, sharesDatabaseVolume: false, configuredBytes: null);

        Assert.Equal(RcloneCacheBudget.DefaultCapBytes, resolved);
    }

    [Fact]
    public void Resolve_TreatsAFullVolumeDifferentlyFromAnUnreadableOne()
    {
        // A volume with nothing left is a real reading, not a failure to read, and
        // must not be handed the 20 GB default.
        var full = RcloneCacheBudget.Resolve(0, sharesDatabaseVolume: false, configuredBytes: null);

        Assert.Equal(RcloneCacheBudget.FloorBytes, full);
        Assert.NotEqual(RcloneCacheBudget.Resolve(null, false, null), full);
    }

    [Fact]
    public void Resolve_TreatsAZeroOrNegativeExplicitLimitAsUnset()
    {
        var free = 4000 * Gib;

        Assert.Equal(
            RcloneCacheBudget.DefaultCapBytes,
            RcloneCacheBudget.Resolve(free, sharesDatabaseVolume: false, configuredBytes: 0));
        Assert.Equal(
            RcloneCacheBudget.DefaultCapBytes,
            RcloneCacheBudget.Resolve(free, sharesDatabaseVolume: false, configuredBytes: -1));
    }

    [Fact]
    public void ResolveFor_SamplesTheVolumeOnlyOnce()
    {
        // The status endpoint resolves this on every poll, so an extra statvfs per
        // call is an extra syscall every few seconds for every viewer.
        var samples = 0;
        var budget = new RcloneCacheBudget
        {
            AvailableFreeSpace = _ =>
            {
                samples++;
                return 100 * Gib;
            },
            MountPointOf = _ => "/mnt/fast",
        };

        budget.ResolveFor("/mnt/fast/cache", "/config", null);

        Assert.Equal(1, samples);
    }
}
