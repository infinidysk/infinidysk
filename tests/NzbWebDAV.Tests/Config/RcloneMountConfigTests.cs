using System.Text.Json;
using NzbWebDAV.Config;

namespace NzbWebDAV.Tests.Config;

public class RcloneMountConfigTests
{
    [Fact]
    public void Deserialize_AppliesDefaults_ForOmittedProperties()
    {
        const string json = """
            [{"Id":"library","MountPoint":"/mnt/remote/infinidysk"}]
            """;

        var mounts = JsonSerializer.Deserialize<List<RcloneMountConfig>>(json)!;

        var mount = Assert.Single(mounts);
        Assert.Equal("library", mount.Id);
        Assert.Equal("/mnt/remote/infinidysk", mount.MountPoint);
        Assert.True(mount.Enabled);
        Assert.Equal("/", mount.RemotePath);
        Assert.Equal(RcloneVfsCacheMode.Full, mount.VfsCacheMode);
        Assert.True(mount.AllowOther);
    }

    [Fact]
    public void Validate_RejectsRelativeMountPoint()
    {
        var mounts = new List<RcloneMountConfig>
        {
            new() { Id = "library", MountPoint = "mnt/remote" },
        };

        var errors = RcloneMountConfig.Validate(mounts, "/config");

        var error = Assert.Single(errors);
        Assert.Contains("absolute", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("library", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/config")]
    [InlineData("/config/mnt")]
    [InlineData("/config/../config/mnt")]
    public void Validate_RejectsMountPointInsideConfigDirectory(string mountPoint)
    {
        var mounts = new List<RcloneMountConfig>
        {
            new() { Id = "library", MountPoint = mountPoint },
        };

        var errors = RcloneMountConfig.Validate(mounts, "/config");

        var error = Assert.Single(errors);
        Assert.Contains("config directory", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllowsMountPointOutsideConfigDirectory()
    {
        var mounts = new List<RcloneMountConfig>
        {
            new() { Id = "library", MountPoint = "/mnt/remote/infinidysk" },
        };

        Assert.Empty(RcloneMountConfig.Validate(mounts, "/config"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app")]
    [InlineData("/usr")]
    [InlineData("/usr/local")]
    [InlineData("/etc")]
    [InlineData("/mnt/../etc")]
    public void Validate_RejectsMountPointsThatWouldShadowTheContainer(string mountPoint)
    {
        // Mounting a FUSE filesystem over /app hides the running application, and
        // the only recovery is recreating the container.
        var errors = RcloneMountConfig.Validate(
            [new RcloneMountConfig { Id = "library", MountPoint = mountPoint }],
            "/config");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_RejectsAMountPointContainingTheConfigDirectory()
    {
        var errors = RcloneMountConfig.Validate(
            [new RcloneMountConfig { Id = "library", MountPoint = "/data" }],
            "/data/config");

        Assert.Contains(errors, e => e.Contains("config directory", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-1L, null, "cache size")]
    [InlineData(null, -1L, "read-ahead")]
    public void Validate_RejectsNegativeSizes(long? cacheSize, long? readAhead, string expected)
    {
        var errors = RcloneMountConfig.Validate(
            [
                new RcloneMountConfig
                {
                    Id = "library",
                    MountPoint = "/mnt/remote/infinidysk",
                    VfsCacheMaxSizeBytes = cacheSize,
                    ReadAheadBytes = readAhead,
                },
            ],
            "/config");

        Assert.Contains(errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsDuplicateIds()
    {
        var mounts = new List<RcloneMountConfig>
        {
            new() { Id = "library", MountPoint = "/mnt/one" },
            new() { Id = "library", MountPoint = "/mnt/two" },
        };

        var error = Assert.Single(RcloneMountConfig.Validate(mounts, "/config"));
        Assert.Contains("library", error, StringComparison.Ordinal);
        Assert.Contains("more than once", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsTwoMountsOnTheSamePath()
    {
        var mounts = new List<RcloneMountConfig>
        {
            new() { Id = "one", MountPoint = "/mnt/remote" },
            new() { Id = "two", MountPoint = "/mnt/remote/" },
        };

        var error = Assert.Single(RcloneMountConfig.Validate(mounts, "/config"));
        Assert.Contains("/mnt/remote", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/mnt/remote", "/mnt/remote/nested")]
    [InlineData("/mnt/remote/nested", "/mnt/remote")]
    public void Validate_RejectsMountsThatContainOneAnother(string first, string second)
    {
        // rclone would be serving one of its own filesystems through the other.
        var errors = RcloneMountConfig.Validate(
            [
                new RcloneMountConfig { Id = "a", MountPoint = first },
                new RcloneMountConfig { Id = "b", MountPoint = second },
            ],
            "/config");

        Assert.Contains(errors, e => e.Contains("overlaps mount", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllowsSiblingMounts()
    {
        var errors = RcloneMountConfig.Validate(
            [
                new RcloneMountConfig { Id = "a", MountPoint = "/mnt/remote" },
                new RcloneMountConfig { Id = "b", MountPoint = "/mnt/remote-other" },
            ],
            "/config");

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_IgnoresDisabledMounts()
    {
        var mounts = new List<RcloneMountConfig>
        {
            new() { Id = "broken", MountPoint = "relative/path", Enabled = false },
        };

        Assert.Empty(RcloneMountConfig.Validate(mounts, "/config"));
    }
}
