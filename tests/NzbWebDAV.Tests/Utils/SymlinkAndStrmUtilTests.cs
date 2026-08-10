using System.Runtime.InteropServices;
using System.Text;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public sealed class SymlinkAndStrmUtilTests
{
    [Fact]
    public void LinuxFindStartInfo_PassesHostileRootAsOneOpaqueArgument()
    {
        var hostileRoot = Path.Join(
            Path.GetTempPath(),
            "-library-\"-'-$()-; touch injected-line1\nline2-ユニコード");

        var startInfo = SymlinkAndStrmUtil.CreateLinuxFindStartInfo(hostileRoot);

        Assert.Equal("find", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(Path.GetFullPath(hostileRoot), startInfo.ArgumentList[1]);
        Assert.Equal(
            ["-H", Path.GetFullPath(hostileRoot), "(", "-type", "l", "-o", "-name", "*.strm", ")", "-print0"],
            startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void ReadNullTerminated_PreservesNewlinesAndRejectsTruncatedRecords()
    {
        using var reader = new StringReader("path/with\nnewline\0next\0");
        Assert.Equal("path/with\nnewline", SymlinkAndStrmUtil.ReadNullTerminated(reader));
        Assert.Equal("next", SymlinkAndStrmUtil.ReadNullTerminated(reader));
        Assert.Null(SymlinkAndStrmUtil.ReadNullTerminated(reader));

        using var truncated = new StringReader("partial-without-nul");
        var error = Assert.Throws<InvalidOperationException>(
            () => SymlinkAndStrmUtil.ReadNullTerminated(truncated));
        Assert.Contains("truncated NUL-terminated path", error.Message);
    }

    [Fact]
    public void ReadStrmTargetUrl_SelectsFirstNonEmptyLineAndBoundsInspection()
    {
        var root = CreateTempDirectory();
        try
        {
            var multiLine = Path.Join(root, "multi.strm");
            File.WriteAllText(multiLine, "\n\r\nhttp://localhost/view/.ids/a.mkv?extension=mkv\nsecond-line\n");

            var withBom = Path.Join(root, "bom.strm");
            File.WriteAllBytes(
                withBom,
                new byte[] { 0xEF, 0xBB, 0xBF }
                    .Concat(Encoding.UTF8.GetBytes("http://localhost/view/.ids/b.mkv\r\n"))
                    .ToArray());

            var empty = Path.Join(root, "empty.strm");
            File.WriteAllText(empty, "");

            var oversized = Path.Join(root, "oversized.strm");
            File.WriteAllText(oversized, new string('a', SymlinkAndStrmUtil.MaxStrmTargetBytes + 1));

            var hugeLaterLine = Path.Join(root, "huge-later.strm");
            File.WriteAllText(
                hugeLaterLine,
                "http://localhost/view/.ids/c.mkv\n" + new string('b', SymlinkAndStrmUtil.MaxStrmTargetBytes * 4));

            Assert.Equal(
                "http://localhost/view/.ids/a.mkv?extension=mkv",
                SymlinkAndStrmUtil.ReadStrmTargetUrl(multiLine));
            Assert.Equal(
                "http://localhost/view/.ids/b.mkv",
                SymlinkAndStrmUtil.ReadStrmTargetUrl(withBom));
            Assert.Null(SymlinkAndStrmUtil.ReadStrmTargetUrl(empty));
            Assert.Equal(
                "http://localhost/view/.ids/c.mkv",
                SymlinkAndStrmUtil.ReadStrmTargetUrl(hugeLaterLine));

            var oversizedError = Assert.Throws<InvalidOperationException>(
                () => SymlinkAndStrmUtil.ReadStrmTargetUrl(oversized));
            Assert.Contains("exceeds", oversizedError.Message);

            Assert.Null(SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(empty)));
            var strmInfo = Assert.IsType<SymlinkAndStrmUtil.StrmInfo>(
                SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(multiLine)));
            Assert.Equal("http://localhost/view/.ids/a.mkv?extension=mkv", strmInfo.TargetUrl);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public void ReadLinuxFindEntry_FailsClosedWhenSymlinkTargetDisappears()
    {
        var root = CreateTempDirectory();
        var path = Path.Join(root, "victim.mkv");
        try
        {
            File.CreateSymbolicLink(path, "original-target.mkv");
            File.Delete(path);
            File.WriteAllText(path, "not-a-symlink-anymore");

            var error = Assert.Throws<InvalidOperationException>(
                () => SymlinkAndStrmUtil.ReadLinuxFindEntry(path));
            Assert.Contains("could not read the symlink target", error.Message);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [SkippableFact]
    public void LinuxEnumeration_HandlesHostileNamesNewlinesAndExactSymlinkTargets()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux find traversal is only used on Linux.");

        var root = Path.Join(
            Path.GetTempPath(),
            $"-library-\"-'-$()-; touch injected-line1\nline2-ユニコード-{Guid.NewGuid():N}");
        var newlineName = "file\nwith\nnewlines.mkv";
        var symlinkPath = Path.Join(root, newlineName);
        var strmPath = Path.Join(root, "movie.strm");
        var trailingSymlinkPath = Path.Join(root, "trailing.mkv");
        const string targetUrl = "http://localhost:8080/view/.ids/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.mkv?extension=mkv";
        const string linkTarget = "missing-target\nwith\nnewlines.mkv";
        const string trailingTarget = "next-target.mkv";

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(strmPath, targetUrl + "\nignored-second-line\n");
            File.CreateSymbolicLink(symlinkPath, linkTarget);
            File.CreateSymbolicLink(trailingSymlinkPath, trailingTarget);

            var results = SymlinkAndStrmUtil.GetAllSymlinksAndStrms(root).ToList();

            var strm = Assert.Single(results.OfType<SymlinkAndStrmUtil.StrmInfo>());
            Assert.Equal(strmPath, strm.StrmPath);
            Assert.Equal(targetUrl, strm.TargetUrl);

            var links = results.OfType<SymlinkAndStrmUtil.SymlinkInfo>()
                .OrderBy(x => x.SymlinkPath, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(2, links.Count);

            var newlineLink = Assert.Single(links, x => x.SymlinkPath == symlinkPath);
            Assert.Equal(linkTarget, newlineLink.TargetPath);

            var trailing = Assert.Single(links, x => x.SymlinkPath == trailingSymlinkPath);
            Assert.Equal(trailingTarget, trailing.TargetPath);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [SkippableFact]
    public void LinuxEnumeration_FollowsSymlinkedRootButNotNestedDirectorySymlinks()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux find traversal is only used on Linux.");

        var parent = CreateTempDirectory();
        var realRoot = Path.Join(parent, "real-root");
        var linkRoot = Path.Join(parent, "link-root");
        var nestedReal = Path.Join(parent, "nested-real");
        var nestedLink = Path.Join(realRoot, "nested-link");

        try
        {
            Directory.CreateDirectory(realRoot);
            Directory.CreateDirectory(nestedReal);
            File.CreateSymbolicLink(linkRoot, realRoot);
            Directory.CreateSymbolicLink(nestedLink, nestedReal);

            File.WriteAllText(Path.Join(realRoot, "root.strm"), "http://localhost/view/.ids/root.mkv");
            File.WriteAllText(Path.Join(nestedReal, "nested.strm"), "http://localhost/view/.ids/nested.mkv");

            var results = SymlinkAndStrmUtil.GetAllSymlinksAndStrms(linkRoot).ToList();
            var strmPaths = results.OfType<SymlinkAndStrmUtil.StrmInfo>()
                .Select(x => x.StrmPath)
                .ToHashSet(StringComparer.Ordinal);

            // find -H may report the strm under the symlink root path or the real path.
            Assert.True(
                strmPaths.Contains(Path.Join(realRoot, "root.strm")) ||
                strmPaths.Contains(Path.Join(linkRoot, "root.strm")),
                $"Expected root.strm under the followed symlink root. Found: {string.Join(", ", strmPaths)}");
            Assert.DoesNotContain(Path.Join(nestedReal, "nested.strm"), strmPaths);
            Assert.DoesNotContain(Path.Join(nestedLink, "nested.strm"), strmPaths);

            // The nested directory symlink itself is still a -type l match.
            Assert.Contains(
                results.OfType<SymlinkAndStrmUtil.SymlinkInfo>(),
                x =>
                    (x.SymlinkPath == nestedLink ||
                     x.SymlinkPath == Path.Join(linkRoot, "nested-link")) &&
                    (x.TargetPath == nestedReal ||
                     Path.GetFullPath(x.TargetPath, Path.GetDirectoryName(x.SymlinkPath)!) == nestedReal));
        }
        finally
        {
            DeleteQuietly(parent);
        }
    }

    [SkippableFact]
    public void LinuxEnumeration_FailsClosedOnPermissionDeniedWithBoundedStderr()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux find traversal is only used on Linux.");
        Skip.If(geteuid() == 0, "Permission-denied traversal cannot be validated as root.");

        var root = CreateTempDirectory();
        var deniedDirs = new List<string>();
        try
        {
            File.WriteAllText(Path.Join(root, "ok.strm"), "http://localhost/view/.ids/ok.mkv");

            // Enough denied subtrees to exceed MaxStderrChars when find reports each one.
            for (var i = 0; i < 80; i++)
            {
                var denied = Path.Join(root, $"denied-{i:D3}");
                Directory.CreateDirectory(denied);
                File.WriteAllText(Path.Join(denied, $"hidden-{i}.strm"), "http://localhost/view/.ids/hidden.mkv");
                chmod(denied, 0);
                deniedDirs.Add(denied);
            }

            var error = Assert.Throws<InvalidOperationException>(
                () => SymlinkAndStrmUtil.GetAllSymlinksAndStrms(root).ToList());
            Assert.Contains("Library symlink scan failed with exit code", error.Message);
            Assert.True(
                error.Message.Length <= 512 + 4096,
                $"Expected bounded stderr in the failure message, got length {error.Message.Length}.");
        }
        finally
        {
            foreach (var denied in deniedDirs)
            {
                try
                {
                    chmod(denied, 0x1FF); // 0777
                }
                catch
                {
                    // Best-effort restore before recursive delete.
                }
            }

            DeleteQuietly(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), $"nzbdav-symlink-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup for tests.
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, int mode);

    [DllImport("libc")]
    private static extern uint geteuid();
}
