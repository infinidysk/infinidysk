using NzbWebDAV.Api.Controllers.UsenetMigration;
using NzbWebDAV.UsenetMigration.Symlinks;

namespace NzbWebDAV.Tests.UsenetMigration;

public class SymlinkBackupAndCsvTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("=cmd", "'=cmd")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@sum", "'@sum")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("=a,b", "\"'=a,b\"")]
    public void Csv_PrefixesFormulaInjectionAndQuotesDelimiters(string input, string expected)
    {
        Assert.Equal(expected, UsenetMigrationController.Csv(input));
    }

    [Fact]
    public async Task WriteAsync_RoundTripsAndSurvivesReadBackVerification()
    {
        var path = Path.Combine(Path.GetTempPath(), $"altmig-bak-{Guid.NewGuid():N}.tar.gz");
        try
        {
            var entries = new[]
            {
                new SymlinkBackup.Entry("/lib/a.mkv", "/alt/a.mkv", "/nzbdav/a.mkv"),
                new SymlinkBackup.Entry("/lib/b.mkv", "/alt/b.mkv", "/nzbdav/b.mkv"),
            };
            await SymlinkBackup.WriteAsync(path, entries);
            var read = await SymlinkBackup.ReadAsync(path);
            Assert.Equal(2, read.Count);
            Assert.Equal(entries[0].Path, read[0].Path);
            Assert.Equal(entries[1].ReplacementTarget, read[1].ReplacementTarget);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [SkippableFact]
    public void ManagedWalker_ReportsSymlinkedDirButDoesNotRecurseIntoIt()
    {
        Skip.If(OperatingSystem.IsLinux(), "Linux path uses find(1); this covers the managed walker.");
        Skip.If(OperatingSystem.IsWindows(), "Directory symlink recursion is validated on macOS.");

        var root = Path.Combine(Path.GetTempPath(), $"altmig-walk-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "real");
        var linked = Path.Combine(root, "linked-dir");
        var outside = Path.Combine(Path.GetTempPath(), $"altmig-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(outside);
        File.CreateSymbolicLink(Path.Combine(nested, "file.link"), "/tmp/target");
        File.CreateSymbolicLink(Path.Combine(outside, "hidden.link"), "/tmp/hidden");
        Directory.CreateSymbolicLink(linked, outside);

        try
        {
            var result = MigrationSymlinkUtil.GetAllSymlinks(root);
            Assert.Contains(result.Links, l => l.SymlinkPath.EndsWith("file.link", StringComparison.Ordinal));
            Assert.Contains(result.Links, l => l.SymlinkPath == linked || l.SymlinkPath.EndsWith("linked-dir", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Links, l => l.SymlinkPath.Contains("hidden.link", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}
