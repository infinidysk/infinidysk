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
