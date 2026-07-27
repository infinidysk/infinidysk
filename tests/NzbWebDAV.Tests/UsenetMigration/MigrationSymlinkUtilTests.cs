using System.Diagnostics;
using NzbWebDAV.UsenetMigration.Symlinks;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class MigrationSymlinkUtilTests
{
    [Fact]
    public void LinuxFindStartInfo_PassesHostileRootAsOneOpaqueArgument()
    {
        var hostileRoot = Path.Combine(
            Path.GetTempPath(),
            "library-\"-'-$()-; touch injected-line1\nline2");

        var startInfo = MigrationSymlinkUtil.CreateLinuxFindStartInfo(hostileRoot);

        Assert.Equal("find", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(
            ["-H", Path.GetFullPath(hostileRoot), "-type", "l", "-print0"],
            startInfo.ArgumentList);
    }

    [SkippableFact]
    public void LinuxEnumeration_HandlesQuotesNewlinesAndShellMetacharactersLiterally()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux find traversal is only used on Linux.");

        var root = Path.Combine(
            Path.GetTempPath(),
            $"library-\"-'-$()-; touch injected-line1\nline2-{Guid.NewGuid():N}");
        var strmPath = Path.Combine(root, "movie.strm");
        var symlinkPath = Path.Combine(root, "episode-\nlink.mkv");
        const string targetUrl = "http://localhost:8080/content/movie.mkv?token=a&part=1";
        const string linkTarget = "missing-target.mkv";

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(strmPath, targetUrl);
            File.CreateSymbolicLink(symlinkPath, linkTarget);

            var result = MigrationSymlinkUtil.GetAllSymlinks(root);

            // readlink returns a dangling link's target without resolving it.
            // A missing target is therefore a normal readable symlink.
            var symlink = Assert.Single(result.Links);
            Assert.Equal(symlinkPath, symlink.SymlinkPath);
            Assert.Equal(linkTarget, symlink.TargetPath);
            Assert.Empty(result.Unreadable);
            Assert.DoesNotContain(result.Links, link => link.SymlinkPath == strmPath);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Enumeration_MissingRootFailsWithoutReturningPartialResults()
    {
        var missingRoot = Path.Combine(
            Path.GetTempPath(),
            $"missing-altmount-library-{Guid.NewGuid():N}");

        Assert.ThrowsAny<Exception>(() => MigrationSymlinkUtil.GetAllSymlinks(missingRoot));
    }

    [Fact]
    public void Enumeration_PreCancelledTokenDoesNotStartWalk()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MigrationSymlinkUtil.GetAllSymlinks(Path.GetTempPath(), cts.Token));
    }

    [SkippableFact]
    public void LinuxEnumeration_NonUtf8Filename_IsReportedNotDropped()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux filenames may contain arbitrary non-UTF-8 bytes.");

        var root = Path.Combine(Path.GetTempPath(), $"altmig-nonutf8-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            CreateNonUtf8Symlink(root);

            var result = MigrationSymlinkUtil.GetAllSymlinks(root);

            Assert.Empty(result.Links);
            var unreadable = Assert.Single(result.Unreadable);
            Assert.StartsWith(root, unreadable.SymlinkPath);
            Assert.False(string.IsNullOrWhiteSpace(unreadable.Reason));
        }
        finally
        {
            DeleteLinuxTree(root);
        }
    }

    [SkippableFact]
    public void LinuxEnumeration_IsACompleteCensus()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux find traversal is only used on Linux.");

        var root = Path.Combine(Path.GetTempPath(), $"altmig-census-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "present.mkv"), "fixture");
            File.CreateSymbolicLink(Path.Combine(root, "readable.mkv"), "present.mkv");
            File.CreateSymbolicLink(Path.Combine(root, "dangling.mkv"), "missing.mkv");
            CreateNonUtf8Symlink(root);

            var result = MigrationSymlinkUtil.GetAllSymlinks(root);

            Assert.Equal(CountFindResults(root), result.Links.Count + result.Unreadable.Count);
            Assert.Equal(2, result.Links.Count);
            Assert.Single(result.Unreadable);
        }
        finally
        {
            DeleteLinuxTree(root);
        }
    }

    private static void CreateNonUtf8Symlink(string root)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("ln -s missing.mkv \"$1/bad-$(printf '\\377\\376')name.mkv\"");
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add(root);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Unable to create non-UTF-8 symlink fixture.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
    }

    private static int CountFindResults(string root)
    {
        using var process = Process.Start(MigrationSymlinkUtil.CreateLinuxFindStartInfo(root))
                            ?? throw new InvalidOperationException("Unable to count symlink fixture entries.");
        var count = 0;
        while (process.StandardOutput.BaseStream.ReadByte() is var next && next >= 0)
        {
            if (next == 0)
                count++;
        }

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return count;
    }

    private static void DeleteLinuxTree(string root)
    {
        if (!Directory.Exists(root))
            return;

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/rm",
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-rf");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(root);
        using var process = Process.Start(startInfo);
        process?.WaitForExit();
    }
}
