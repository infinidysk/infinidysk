using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// Lowering the cache ceiling does not reclaim what is already on disk, and the
/// only alternative was deleting files by hand inside the container. The purge
/// has to be narrow: it empties the configured cache directory and touches
/// nothing else.
/// </summary>
public class RcloneCachePurgerTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        $"rclone-cache-purge-{Guid.NewGuid():N}");

    [Fact]
    public void Purge_EmptiesTheCacheDirectoryButKeepsIt()
    {
        var cacheDir = Path.Join(_root, "cache");
        Directory.CreateDirectory(Path.Join(cacheDir, "vfs", "infinidysk"));
        File.WriteAllText(Path.Join(cacheDir, "vfs", "infinidysk", "chunk"), new string('x', 1024));

        var result = RcloneCachePurger.Purge(cacheDir);

        Assert.True(Directory.Exists(cacheDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cacheDir));
        Assert.Equal(1024, result.FreedBytes);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Purge_KeepsEverythingRcloneDidNotWrite()
    {
        // The cache directory is operator-set and can be pointed at a directory
        // that already holds something else -- the config directory, a media
        // root. Only rclone's own subtrees are the purge's to delete; a database
        // sitting beside them must still be there afterwards.
        var cacheDir = Path.Join(_root, "config");
        Directory.CreateDirectory(Path.Join(cacheDir, "vfs", "infinidysk"));
        Directory.CreateDirectory(Path.Join(cacheDir, "vfsMeta", "infinidysk"));
        Directory.CreateDirectory(Path.Join(cacheDir, "backups"));
        File.WriteAllText(Path.Join(cacheDir, "vfs", "infinidysk", "chunk"), new string('x', 512));
        File.WriteAllText(Path.Join(cacheDir, "vfsMeta", "infinidysk", "meta"), new string('x', 512));
        File.WriteAllText(Path.Join(cacheDir, "db.sqlite"), "not rclone's");
        File.WriteAllText(Path.Join(cacheDir, "backups", "db.bak"), "not rclone's either");

        var result = RcloneCachePurger.Purge(cacheDir);

        Assert.False(Directory.Exists(Path.Join(cacheDir, "vfs")));
        Assert.False(Directory.Exists(Path.Join(cacheDir, "vfsMeta")));
        Assert.True(File.Exists(Path.Join(cacheDir, "db.sqlite")));
        Assert.True(File.Exists(Path.Join(cacheDir, "backups", "db.bak")));
        Assert.Equal(1024, result.FreedBytes);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Purge_ReportsNothingToDo_WhenTheCacheHoldsNoRcloneData()
    {
        // Same directory, nothing of rclone's in it yet: there is nothing to
        // free, and nothing to delete.
        var cacheDir = Path.Join(_root, "shared");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Join(cacheDir, "db.sqlite"), "not rclone's");

        var result = RcloneCachePurger.Purge(cacheDir);

        Assert.True(File.Exists(Path.Join(cacheDir, "db.sqlite")));
        Assert.Equal(0, result.FreedBytes);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Purge_DoesNotFollowASymlinkedCacheSubtree()
    {
        // A cache directory holding a link named "vfs" must not turn the purge
        // into a delete of whatever it points at.
        if (!OperatingSystem.IsLinux()) return;

        var cacheDir = Path.Join(_root, "linked");
        var elsewhere = Path.Join(_root, "elsewhere");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Join(elsewhere, "db.sqlite"), "not rclone's");
        Directory.CreateSymbolicLink(Path.Join(cacheDir, "vfs"), elsewhere);

        var result = RcloneCachePurger.Purge(cacheDir);

        Assert.True(File.Exists(Path.Join(elsewhere, "db.sqlite")));
        Assert.True(Directory.Exists(Path.Join(cacheDir, "vfs")));
        Assert.Equal(0, result.FreedBytes);
    }

    [Fact]
    public void Purge_ReportsNothingToDo_WhenTheDirectoryDoesNotExist()
    {
        // A cache directory that was never written to is not an error; rclone
        // creates it on demand.
        var result = RcloneCachePurger.Purge(Path.Join(_root, "missing"));

        Assert.Equal(0, result.FreedBytes);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Purge_RefusesAPathThatIsNotAbsolute()
    {
        var result = RcloneCachePurger.Purge("rclone/cache");

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Purge_RefusesAFilesystemRoot()
    {
        // A misconfigured cache directory must never turn this into a wipe.
        var result = RcloneCachePurger.Purge("/");

        Assert.NotNull(result.Error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
