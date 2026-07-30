using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.Tests.UsenetMigration;

public class MetadataTreeWalkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"meta-walk-{Guid.NewGuid():N}");

    public MetadataTreeWalkerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void EnumerateMetaFiles_SkipsIdsAndCorruptedMetadataDirs()
    {
        var keep = Path.Combine(_root, "tv", "Show.mkv.meta");
        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
        File.WriteAllText(keep, "ok");

        var ids = Path.Combine(_root, ".ids", "a", "b", "c", "d", "e");
        Directory.CreateDirectory(ids);
        File.WriteAllText(Path.Combine(ids, $"{Guid.NewGuid()}.meta"), "id");

        var corrupted = Path.Combine(_root, "corrupted_metadata", "tv");
        Directory.CreateDirectory(corrupted);
        File.WriteAllText(Path.Combine(corrupted, "bad.mkv.meta"), "bad");

        var found = MetadataTreeWalker.EnumerateMetaFiles(_root).ToList();
        Assert.Single(found);
        Assert.Equal(keep, found[0]);
    }

    [Fact]
    public void EnumerateMetaFiles_SkipsSymlinkedMetaFiles()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var real = Path.Combine(_root, "real.mkv.meta");
        File.WriteAllText(real, "ok");
        var link = Path.Combine(_root, "link.mkv.meta");
        File.CreateSymbolicLink(link, real);

        var found = MetadataTreeWalker.EnumerateMetaFiles(_root).ToList();
        Assert.Single(found);
        Assert.Equal(real, found[0]);
    }

    [Fact]
    public void EnumerateMetaFiles_DoesNotFollowDirectorySymlinks()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var outside = Path.Combine(Path.GetTempPath(), $"meta-walk-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "escaped.mkv.meta"), "x");
            var linkDir = Path.Combine(_root, "linked");
            Directory.CreateSymbolicLink(linkDir, outside);

            File.WriteAllText(Path.Combine(_root, "local.mkv.meta"), "ok");
            var found = MetadataTreeWalker.EnumerateMetaFiles(_root).ToList();
            Assert.Single(found);
            Assert.EndsWith("local.mkv.meta", found[0]);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void EnumerateMetaFiles_ReportsUnreadableDirsViaCallback()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "x.meta"), "x");
        // Remove all permissions so GetFiles/GetDirectories fail.
        File.SetUnixFileMode(locked, (UnixFileMode)0);

        var errors = new List<(string Path, string Message)>();
        try
        {
            _ = MetadataTreeWalker.EnumerateMetaFiles(
                _root,
                (path, message) => errors.Add((path, message))).ToList();
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Contains(errors, e => e.Path == locked && !string.IsNullOrEmpty(e.Message));
    }
}
