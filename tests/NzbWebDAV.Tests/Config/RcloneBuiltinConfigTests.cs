using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class RcloneBuiltinConfigTests
{
    [Fact]
    public void Defaults_AreOffWithNoMounts()
    {
        var config = new ConfigManager();

        Assert.False(config.IsRcloneBuiltinEnabled());
        Assert.Empty(config.GetRcloneBuiltinMounts());
        Assert.Equal(ConfigManager.DefaultRcloneBuiltinRcPort, config.GetRcloneBuiltinRcPort());
    }

    [Fact]
    public void GetRcloneBuiltinMounts_ParsesConfiguredJson()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinMounts,
                ConfigValue = """
                    [{"Id":"library","MountPoint":"/mnt/remote/infinidysk","VfsCacheMode":"Writes"}]
                    """,
            },
        ]);

        var mount = Assert.Single(config.GetRcloneBuiltinMounts());
        Assert.Equal("library", mount.Id);
        Assert.Equal("/mnt/remote/infinidysk", mount.MountPoint);
        Assert.Equal(RcloneVfsCacheMode.Writes, mount.VfsCacheMode);
    }

    [Fact]
    public void GetRcloneBuiltinMounts_ReturnsEmpty_WhenJsonIsUnparseable()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneBuiltinMounts, ConfigValue = "not json" },
        ]);

        Assert.Empty(config.GetRcloneBuiltinMounts());
    }

    [Fact]
    public void ValidateConfigItems_RejectsMountsThatFailValidation()
    {
        var items = new[]
        {
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinMounts,
                ConfigValue = """[{"Id":"library","MountPoint":"relative/path"}]""",
            },
        };

        var exception = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(items));
        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateConfigItems_RejectsAnRcPortOutsideTheValidRange()
    {
        var items = new[]
        {
            new ConfigItem { ConfigName = ConfigKeys.RcloneBuiltinRcPort, ConfigValue = "0" },
        };

        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(items));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("70000")]
    [InlineData("not-a-port")]
    public void GetRcloneBuiltinRcPort_FallsBackWhenThePersistedValueIsUnusable(string stored)
    {
        // Save-time validation rejects these, but a value can still reach the
        // database from an older release or a hand-edited row, and binding to it
        // fails in a way that looks like a broken daemon.
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneBuiltinRcPort, ConfigValue = stored },
        ]);

        Assert.Equal(ConfigManager.DefaultRcloneBuiltinRcPort, config.GetRcloneBuiltinRcPort());
    }

    [Fact]
    public void ValidateConfigItems_RejectsARelativeCacheDirectory()
    {
        var error = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneBuiltinCacheDir, ConfigValue = "rclone/cache" },
        ]));

        Assert.Contains("absolute path", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateConfigItems_RejectsACacheDirectoryInsideAMount()
    {
        // rclone would cache the filesystem it is serving into itself.
        var error = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinMounts,
                ConfigValue = """[{"Id":"library","MountPoint":"/mnt/remote/infinidysk"}]""",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinCacheDir,
                ConfigValue = "/mnt/remote/infinidysk/cache",
            },
        ]));

        Assert.Contains("cache", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateConfigItems_AcceptsACacheDirectoryOutsideEveryMount()
    {
        ConfigManager.ValidateConfigItems(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinMounts,
                ConfigValue = """[{"Id":"library","MountPoint":"/mnt/remote/infinidysk"}]""",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinCacheDir,
                ConfigValue = "/mnt/fast/rclone-cache",
            },
        ]);
    }

    [Fact]
    public void ValidateConfigItems_ReportsMalformedMountJson_WhateverOrderItArrivesIn()
    {
        // The settings page decides the order of the items it sends. The cache
        // directory's own check reads the mount list, so a malformed value must
        // still surface as a validation error rather than a raw JsonException.
        var error = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinCacheDir,
                ConfigValue = "/mnt/fast/rclone-cache",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinMounts,
                ConfigValue = "{not json",
            },
        ]));

        Assert.Contains(ConfigKeys.RcloneBuiltinMounts, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConfigItems_RejectsACacheSizeLimitTooSmallToStreamFrom()
    {
        var error = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneBuiltinCacheSizeLimit, ConfigValue = "1048576" },
        ]));

        Assert.Contains("cache", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateConfigItems_AcceptsAnEmptyCacheSizeLimit()
    {
        // Empty is how the operator asks for the automatic size again.
        ConfigManager.ValidateConfigItems(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneBuiltinCacheSizeLimit, ConfigValue = "" },
        ]);
    }

    [Fact]
    public void GetRcloneBuiltinCacheSizeLimit_IsNullUntilOneIsSet()
    {
        Assert.Null(new ConfigManager().GetRcloneBuiltinCacheSizeLimit());
    }

    [Fact]
    public void GetRcloneBuiltinCacheSizeLimit_ReturnsTheConfiguredCeiling()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinCacheSizeLimit,
                ConfigValue = "8589934592",
            },
        ]);

        Assert.Equal(8_589_934_592L, config.GetRcloneBuiltinCacheSizeLimit());
    }

    [Fact]
    public void GetAllRcloneMountDirs_CoversTheSymlinkRootAndEveryBuiltinMountPoint()
    {
        // Maintenance tasks guard against a library directory sitting inside a
        // mount. With built-in mode there is more than one candidate.
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = "/mnt/nzbdav" },
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinMounts,
                ConfigValue = """[{"Id":"library","MountPoint":"/data/nzbdav","RemotePath":"/"}]""",
            },
        ]);

        Assert.Equal(["/mnt/nzbdav", "/data/nzbdav"], config.GetAllRcloneMountDirs());
    }

    [Fact]
    public void GetAllRcloneMountDirs_ListsTheSymlinkRootOnce_WhenABuiltinMountUsesIt()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = "/mnt/nzbdav" },
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinMounts,
                ConfigValue = """[{"Id":"library","MountPoint":"/mnt/nzbdav","RemotePath":"/"}]""",
            },
        ]);

        Assert.Equal(["/mnt/nzbdav"], config.GetAllRcloneMountDirs());
    }

    [Fact]
    public void GetRcloneBuiltinCacheDir_FallsBackUnderTheConfigPath()
    {
        var config = new ConfigManager();

        Assert.EndsWith(Path.Join("rclone", "cache"), config.GetRcloneBuiltinCacheDir(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetRcloneBuiltinCacheDir_UsesTheConfiguredDirectory()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneBuiltinCacheDir, ConfigValue = "/cache/rclone" },
        ]);

        Assert.Equal("/cache/rclone", config.GetRcloneBuiltinCacheDir());
    }

    [Theory]
    [InlineData(ConfigKeys.RcloneBuiltinEnabled, "NZBDAV_CONFIG__RCLONE__BUILTIN__ENABLED")]
    [InlineData(ConfigKeys.RcloneBuiltinMounts, "NZBDAV_CONFIG__RCLONE__BUILTIN__MOUNTS")]
    [InlineData(ConfigKeys.RcloneBuiltinRcPort, "NZBDAV_CONFIG__RCLONE__BUILTIN__RC_PORT")]
    [InlineData(ConfigKeys.RcloneBuiltinCacheDir, "NZBDAV_CONFIG__RCLONE__BUILTIN__CACHE_DIR")]
    [InlineData(
        ConfigKeys.RcloneBuiltinCacheSizeLimit,
        "NZBDAV_CONFIG__RCLONE__BUILTIN__CACHE_SIZE_LIMIT")]
    public void BuiltinKeys_AreReachableFromTheHeadlessEnvironmentNamespace(string configKey, string expected)
    {
        Assert.Equal(expected, ConfigEnvMapping.ToEnvironmentVariableName(configKey));
    }

    [Fact]
    public void ValidateConfigItems_RefusesACacheDirectoryInsideAnAlreadySavedMount()
    {
        // The settings page can save the cache directory on its own. Validating
        // only the keys in the request let a cache directory inside a mount
        // through -- rclone caching the filesystem into itself -- purely because
        // the mount list was not part of that request.
        var saved = new Dictionary<string, string>
        {
            [ConfigKeys.RcloneBuiltinMounts] =
                """[{"Id":"library","MountPoint":"/mnt/remote/infinidysk"}]""",
        };

        var items = new[]
        {
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinCacheDir,
                ConfigValue = "/mnt/remote/infinidysk/cache",
            },
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems(items, savedValue: key => saved.GetValueOrDefault(key)));

        Assert.Contains("inside the mount point", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConfigItems_RefusesAMountThatWouldSwallowTheSavedCacheDirectory()
    {
        // The same check from the other side: saving only the mount list.
        var saved = new Dictionary<string, string>
        {
            [ConfigKeys.RcloneBuiltinCacheDir] = "/mnt/remote/infinidysk/cache",
        };

        var items = new[]
        {
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinMounts,
                ConfigValue = """[{"Id":"library","MountPoint":"/mnt/remote/infinidysk"}]""",
            },
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems(items, savedValue: key => saved.GetValueOrDefault(key)));

        Assert.Contains("inside the mount point", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConfigItems_AcceptsACacheDirectoryOutsideTheSavedMounts()
    {
        var saved = new Dictionary<string, string>
        {
            [ConfigKeys.RcloneBuiltinMounts] =
                """[{"Id":"library","MountPoint":"/mnt/remote/infinidysk"}]""",
        };

        var items = new[]
        {
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneBuiltinCacheDir,
                ConfigValue = "/config/rclone/cache",
            },
        };

        ConfigManager.ValidateConfigItems(items, savedValue: key => saved.GetValueOrDefault(key));
    }
}
