using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class AltmountPathDetectorTests
{
    [Fact]
    public async Task Detect_ResolvesStandardLayoutFromCustomRoot()
    {
        var root = Directory.CreateTempSubdirectory("altmig-detect-");
        try
        {
            var metadataRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "metadata"));
            var configPath = Path.Combine(root.FullName, "config.yaml");
            await File.WriteAllTextAsync(
                configPath,
                "sabnzbd:\n  categories:\n  - name: 'movies'\n    dir: 'movies'\n");

            var result = await AltmountPathDetector.DetectAsync(
                root.FullName + Path.DirectorySeparatorChar);

            Assert.True(result.Detected);
            Assert.Null(result.Reason);
            Assert.Equal(Path.TrimEndingDirectorySeparator(root.FullName), result.Root);
            Assert.Equal(metadataRoot.FullName, result.MetadataRoot);
            Assert.Equal(configPath, result.ConfigPath);
            Assert.Equal(result.Root, result.StoreRoot);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Detect_ReportsMissingRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"missing-altmig-detect-{Guid.NewGuid():N}");

        var result = await AltmountPathDetector.DetectAsync(root);

        Assert.False(result.Detected);
        Assert.Equal(AltmountPathDetector.FailureReason, result.Reason);
        Assert.DoesNotContain(root, result.Reason);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "metadata"), result.MetadataRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "config.yaml"), result.ConfigPath);
        Assert.Equal(Path.GetFullPath(root), result.StoreRoot);
    }

    [Fact]
    public async Task Detect_DoesNotReportWhichStandardLayoutEntryIsMissing()
    {
        var root = Directory.CreateTempSubdirectory("altmig-detect-");
        try
        {
            var result = await AltmountPathDetector.DetectAsync(root.FullName);

            Assert.False(result.Detected);
            Assert.Equal(AltmountPathDetector.FailureReason, result.Reason);
            Assert.DoesNotContain(root.FullName, result.Reason);
            Assert.DoesNotContain("metadata", result.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("config", result.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Detect_RequiresConfigAlongsideExistingMetadata()
    {
        var root = Directory.CreateTempSubdirectory("altmig-detect-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "metadata"));

            var result = await AltmountPathDetector.DetectAsync(root.FullName);

            Assert.False(result.Detected);
            Assert.Equal(AltmountPathDetector.FailureReason, result.Reason);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../../../../etc")]
    public async Task Detect_RejectsBlankOrRelativeRoots(string root)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => AltmountPathDetector.DetectAsync(root));
    }

    [Fact]
    public async Task Detect_RejectsNavigationSegmentsBeforeCanonicalization()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"altmig-parent-{Guid.NewGuid():N}");
        var withParentTraversal = Path.Combine(parent, "child", "..", "altmount");
        var withCurrentTraversal = Path.Combine(parent, ".", "altmount");

        await Assert.ThrowsAsync<ArgumentException>(
            () => AltmountPathDetector.DetectAsync(withParentTraversal));
        await Assert.ThrowsAsync<ArgumentException>(
            () => AltmountPathDetector.DetectAsync(withCurrentTraversal));
    }

    [Fact]
    public async Task Detect_RejectsFilesystemRoot()
    {
        var fileSystemRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));

        Assert.False(string.IsNullOrEmpty(fileSystemRoot));
        await Assert.ThrowsAsync<ArgumentException>(
            () => AltmountPathDetector.DetectAsync(fileSystemRoot));
    }

    [Fact]
    public async Task Detect_ReportsReadableConfigWithoutCategories()
    {
        var root = Directory.CreateTempSubdirectory("altmig-detect-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "metadata"));
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, "config.yaml"),
                "sabnzbd:\n  categories:\n");

            var result = await AltmountPathDetector.DetectAsync(root.FullName);

            Assert.False(result.Detected);
            Assert.Equal(AltmountPathDetector.NoCategoriesReason, result.Reason);
            Assert.DoesNotContain(root.FullName, result.Reason);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Detect_ReportsUnsupportedConfigWithoutEchoingPath()
    {
        var root = Directory.CreateTempSubdirectory("altmig-detect-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "metadata"));
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, "config.yaml"),
                "sabnzbd:\n  categories: [{ name: 'movies' }]\n");

            var result = await AltmountPathDetector.DetectAsync(root.FullName);

            Assert.False(result.Detected);
            Assert.Equal(AltmountPathDetector.InvalidConfigReason, result.Reason);
            Assert.DoesNotContain(root.FullName, result.Reason);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
