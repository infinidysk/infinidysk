using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RcloneMountCommandParserTests
{
    // The sidecar command from docs/guides/mounting-webdav.md, which is what a
    // user is most likely to paste.
    private const string DocumentedCommand = """
        rclone mount nzbdav: /mnt/remote/nzbdav
          --cache-dir=/cache
          --uid=1000
          --gid=1000
          --allow-other
          --links
          --use-cookies
          --vfs-cache-mode=full
          --vfs-cache-max-size=20G
          --vfs-cache-max-age=24h
          --buffer-size=0M
          --vfs-read-ahead=512M
          --dir-cache-time=20s
        """;

    [Fact]
    public void Parse_ReadsTheMountPointAndRemotePath()
    {
        var result = RcloneMountCommandParser.Parse(DocumentedCommand);

        Assert.True(result.Success);
        Assert.Equal("/mnt/remote/nzbdav", result.Mount!.MountPoint);
        Assert.Equal("/", result.Mount.RemotePath);
    }

    [Fact]
    public void Parse_ReadsTheTuningFlags()
    {
        var mount = RcloneMountCommandParser.Parse(DocumentedCommand).Mount!;

        Assert.Equal(RcloneVfsCacheMode.Full, mount.VfsCacheMode);
        Assert.Equal(20L * 1024 * 1024 * 1024, mount.VfsCacheMaxSizeBytes);
        Assert.Equal(TimeSpan.FromHours(24), mount.VfsCacheMaxAge);
        Assert.Equal(512L * 1024 * 1024, mount.ReadAheadBytes);
        Assert.Equal(TimeSpan.FromSeconds(20), mount.DirCacheTime);
        Assert.True(mount.AllowOther);
        Assert.True(mount.Links);
    }

    [Fact]
    public void Parse_AcceptsSpaceSeparatedFlagValues()
    {
        var result = RcloneMountCommandParser.Parse(
            "rclone mount nzbdav:/content /mnt/x --vfs-cache-mode writes --dir-cache-time 5m");

        Assert.Equal(RcloneVfsCacheMode.Writes, result.Mount!.VfsCacheMode);
        Assert.Equal(TimeSpan.FromMinutes(5), result.Mount.DirCacheTime);
        Assert.Equal("/content", result.Mount.RemotePath);
    }

    [Fact]
    public void Parse_ReportsFlagsItDoesNotModel()
    {
        var result = RcloneMountCommandParser.Parse(DocumentedCommand);

        // Nothing applies these, so they are reported rather than persisted:
        // claiming to keep a flag that is never passed to rclone would make an
        // import look faithful when it is not.
        Assert.Contains("--use-cookies", result.UnsupportedFlags);
        Assert.Contains("--uid=1000", result.UnsupportedFlags);
    }

    [Fact]
    public void Parse_DoesNotLetAnUnknownBooleanFlagSwallowTheRemote()
    {
        var result = RcloneMountCommandParser.Parse("rclone mount --read-only nzbdav: /mnt/x");

        Assert.True(result.Success);
        Assert.Equal("/mnt/x", result.Mount!.MountPoint);
        Assert.Contains("--read-only", result.UnsupportedFlags);
    }

    [Fact]
    public void Parse_TreatsShortFlagsAsFlags()
    {
        // "-vv" is not a setting this parser models, but reading it as a
        // positional argument would push the mount point out of position.
        var result = RcloneMountCommandParser.Parse("rclone mount -vv nzbdav: /mnt/x");

        Assert.True(result.Success);
        Assert.Equal("/mnt/x", result.Mount!.MountPoint);
        Assert.Contains("-vv", result.UnsupportedFlags);
    }

    [Fact]
    public void Parse_RefusesToGuessWhenAnUnknownFlagTakesASeparateValue()
    {
        // "002" could be the value of --umask or the mount point. Guessing wrong
        // mounts the library somewhere else and breaks every imported symlink.
        var result = RcloneMountCommandParser.Parse("rclone mount --umask 002 nzbdav: /mnt/x");

        Assert.False(result.Success);
        Assert.Contains("--umask", result.Error!, StringComparison.Ordinal);
        Assert.Contains("--flag=value", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_KeepsRcloneOwnCacheModeDefault_WhenTheCommandDidNotSetOne()
    {
        // rclone's default is "off"; importing it as "full" would silently change
        // how much disk the mount uses.
        var mount = RcloneMountCommandParser.Parse("rclone mount nzbdav: /mnt/x").Mount!;

        Assert.Equal(RcloneVfsCacheMode.Off, mount.VfsCacheMode);
    }

    [Fact]
    public void Parse_DefaultsAllowOtherToFalse_WhenTheCommandDidNotAskForIt()
    {
        var mount = RcloneMountCommandParser.Parse("rclone mount nzbdav: /mnt/x").Mount!;

        Assert.False(mount.AllowOther);
        Assert.False(mount.Links);
    }

    [Fact]
    public void Parse_RejectsACommandThatIsNotAnRcloneMount()
    {
        var result = RcloneMountCommandParser.Parse("docker compose up -d");

        Assert.False(result.Success);
        Assert.Contains("rclone mount", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsACommandMissingTheMountPoint()
    {
        var result = RcloneMountCommandParser.Parse("rclone mount nzbdav:");

        Assert.False(result.Success);
        Assert.Contains("mount point", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("512M", 512L * 1024 * 1024)]
    [InlineData("20G", 20L * 1024 * 1024 * 1024)]
    [InlineData("1024", 1024L)]
    [InlineData("0M", 0L)]
    [InlineData("1.5G", (long)(1.5 * 1024 * 1024 * 1024))]
    public void ParseSize_UnderstandsRcloneSuffixes(string value, long expected)
    {
        Assert.True(RcloneMountCommandParser.TryParseSize(value, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("20s", 20)]
    [InlineData("5m", 300)]
    [InlineData("24h", 86400)]
    [InlineData("1h30m", 5400)]
    public void ParseDuration_UnderstandsRcloneDurations(string value, int expectedSeconds)
    {
        Assert.True(RcloneMountCommandParser.TryParseDuration(value, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), duration);
    }
}
