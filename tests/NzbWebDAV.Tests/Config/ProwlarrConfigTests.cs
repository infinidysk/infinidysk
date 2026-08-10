using System.Collections;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class ProwlarrConfigTests
{
    [Fact]
    public void EnvironmentOverlay_CanManageProwlarrSettingsIndependently()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.ProwlarrUrl, ConfigValue = "http://sqlite-prowlarr:9696" },
            new ConfigItem { ConfigName = ConfigKeys.ProwlarrApiKey, ConfigValue = "sqlite-key" },
        ]);
        config.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__PROWLARR__URL"] = "http://env-prowlarr:9696/base/",
            ["NZBDAV_CONFIG__PROWLARR__SYNC_INTERVAL_MINUTES"] = "30",
        }));

        Assert.Equal("http://env-prowlarr:9696/base", config.GetProwlarrUrl());
        Assert.Equal("sqlite-key", config.GetProwlarrApiKey());
        Assert.False(config.IsProwlarrSyncEnabled());
        Assert.Equal(30, config.GetProwlarrSyncIntervalMinutes());
        Assert.True(config.IsEnvironmentManaged(ConfigKeys.ProwlarrUrl));
        Assert.False(config.IsEnvironmentManaged(ConfigKeys.ProwlarrApiKey));
        Assert.Equal(
            "NZBDAV_CONFIG__PROWLARR__URL",
            config.GetEnvironmentVariableName(ConfigKeys.ProwlarrUrl));
    }

    [Theory]
    [InlineData("https://user:secret@prowlarr:9696")]
    [InlineData("http://prowlarr:9696/?x=1")]
    [InlineData("ftp://prowlarr:9696")]
    public void ProwlarrUrl_RejectsCredentialsQueryAndUnsupportedSchemes(string url)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems([
                new ConfigItem { ConfigName = ConfigKeys.ProwlarrUrl, ConfigValue = url },
            ]));

        Assert.Contains(ConfigKeys.ProwlarrUrl, ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("10081")]
    [InlineData("1.5")]
    public void ProwlarrSyncInterval_UsesSharedValidationBounds(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems([
                new ConfigItem { ConfigName = ConfigKeys.ProwlarrSyncIntervalMinutes, ConfigValue = value },
            ]));
    }

    [Fact]
    public void ProwlarrSyncStatus_RoundTripsThroughConfigManager()
    {
        var config = new ConfigManager();
        const string json = """
            {"LastAttemptAt":100,"LastSuccessAt":99,"RemoteIndexerCount":2,"Added":1,"Updated":1,"Removed":0,"Skipped":0}
            """;

        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.ProwlarrSyncStatus, ConfigValue = json },
        ]);

        var status = config.GetProwlarrSyncStatus();
        Assert.Equal(100, status.LastAttemptAt);
        Assert.Equal(99, status.LastSuccessAt);
        Assert.Equal(2, status.RemoteIndexerCount);
        Assert.Equal(1, status.Added);
    }
}
