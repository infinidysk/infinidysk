using NzbWebDAV.Api.Controllers.UsenetMigration;
using NzbWebDAV.Database.Models.UsenetMigration;
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
    public void ShQuote_EscapesEmbeddedSingleQuotes()
    {
        Assert.Equal("'plain'", UsenetMigrationController.ShQuote("plain"));
        Assert.Equal("'it'\\''s'", UsenetMigrationController.ShQuote("it's"));
        Assert.Equal("''", UsenetMigrationController.ShQuote(""));
    }

    [Fact]
    public void BuildShellScript_EmitsOnlyRewriteRowsWithDriftGuard()
    {
        var rows = new[]
        {
            new MigrationSymlinkRewrite
            {
                SymlinkPath = "/lib/a.mkv",
                OldTarget = "/alt/a.mkv",
                NewTarget = "/nzbdav/a.mkv",
                Status = "rewrite",
            },
            new MigrationSymlinkRewrite
            {
                SymlinkPath = "/lib/orphan.mkv",
                OldTarget = "/alt/o.mkv",
                NewTarget = null,
                Status = "orphan",
            },
            new MigrationSymlinkRewrite
            {
                SymlinkPath = "/lib/it's.mkv",
                OldTarget = "/alt/it's.mkv",
                NewTarget = "/nzbdav/it's.mkv",
                Status = "rewrite",
            },
        };

        var script = System.Text.Encoding.UTF8.GetString(UsenetMigrationController.BuildShellScript(rows));
        Assert.Contains("ln -sfn", script);
        Assert.Contains("SKIP (drifted)", script);
        Assert.Contains("readlink", script);
        Assert.Contains("/lib/a.mkv", script);
        Assert.Contains("'\\''", script); // embedded quote escaping
        Assert.DoesNotContain("orphan.mkv", script);
    }

    [Fact]
    public void DefaultSymlinkBackupDir_IsUnderConfigPath()
    {
        var previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", "/tmp/nzbdav-config-test");
            Assert.Equal(
                Path.Join("/tmp/nzbdav-config-test", "migration-backups"),
                UsenetMigrationController.DefaultSymlinkBackupDir());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", previous);
        }
    }

    [Fact]
    public async Task WriteAsync_RoundTripsAndSurvivesReadBackVerification()
    {
        var path = Path.Join(Path.GetTempPath(), $"altmig-bak-{Guid.NewGuid():N}.tar.gz");
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

        var root = Path.Join(Path.GetTempPath(), $"altmig-walk-{Guid.NewGuid():N}");
        var nested = Path.Join(root, "real");
        var linked = Path.Join(root, "linked-dir");
        var outside = Path.Join(Path.GetTempPath(), $"altmig-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(outside);
        File.CreateSymbolicLink(Path.Join(nested, "file.link"), "/tmp/target");
        File.CreateSymbolicLink(Path.Join(outside, "hidden.link"), "/tmp/hidden");
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
