using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class ExternalMetadataResponseLimitsConfigTests
{
    [Fact]
    public void WatchtowerGetter_UsesDefaultWhenUnset()
    {
        var config = new ConfigManager();
        Assert.Equal(
            ExternalMetadataResponseLimits.WatchtowerDefaultMaxResponseBytes,
            config.GetWatchtowerListSourceMaxResponseBytes());
    }

    [Fact]
    public void WatchtowerGetter_ClampsAboveHardMax()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKeys.WatchtowerListSourceMaxResponseBytes,
                ConfigValue = (ExternalMetadataResponseLimits.HardMaxResponseBytes + 1).ToString(),
            },
        ]);

        Assert.Equal(
            ExternalMetadataResponseLimits.HardMaxResponseBytes,
            config.GetWatchtowerListSourceMaxResponseBytes());
    }

    [Fact]
    public void WatchtowerGetter_ZeroFallsBackToDefault()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKeys.WatchtowerListSourceMaxResponseBytes,
                ConfigValue = "0",
            },
        ]);

        Assert.Equal(
            ExternalMetadataResponseLimits.WatchtowerDefaultMaxResponseBytes,
            config.GetWatchtowerListSourceMaxResponseBytes());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("16777217")]
    public void WatchtowerSave_RejectsOutOfRange(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems([
                new ConfigItem
                {
                    ConfigName = ConfigKeys.WatchtowerListSourceMaxResponseBytes,
                    ConfigValue = value,
                },
            ]));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("8388608")]
    [InlineData("16777216")]
    public void WatchtowerSave_AcceptsBounds(string value)
    {
        ConfigManager.ValidateConfigItems([
            new ConfigItem
            {
                ConfigName = ConfigKeys.WatchtowerListSourceMaxResponseBytes,
                ConfigValue = value,
            },
        ]);
    }

    [Fact]
    public void IndexerConfig_GetEffective_UsesDefaultThenPerIndexerThenClamp()
    {
        var cfg = new IndexerConfig();
        var indexer = new IndexerConfig.ConnectionDetails
        {
            Name = "A",
            Url = "http://example/api",
            ApiKey = "k",
        };
        Assert.Equal(ExternalMetadataResponseLimits.NewznabDefaultMaxResponseBytes, cfg.GetEffectiveMaxResponseBytes(indexer));

        cfg.MaxResponseBytes = 1024;
        Assert.Equal(1024, cfg.GetEffectiveMaxResponseBytes(indexer));

        indexer.MaxResponseBytes = 2048;
        Assert.Equal(2048, cfg.GetEffectiveMaxResponseBytes(indexer));

        indexer.MaxResponseBytes = ExternalMetadataResponseLimits.HardMaxResponseBytes + 5;
        Assert.Equal(ExternalMetadataResponseLimits.HardMaxResponseBytes, cfg.GetEffectiveMaxResponseBytes(indexer));
    }

    [Fact]
    public void IndexerInstances_RejectsZeroAndOverHardMax()
    {
        var tooBig = JsonSerializer.Serialize(new IndexerConfig
        {
            MaxResponseBytes = ExternalMetadataResponseLimits.HardMaxResponseBytes + 1,
            Indexers = [],
        });
        Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems([
                new ConfigItem { ConfigName = ConfigKeys.IndexersInstances, ConfigValue = tooBig },
            ]));

        var zero = JsonSerializer.Serialize(new IndexerConfig
        {
            Indexers =
            [
                new IndexerConfig.ConnectionDetails
                {
                    Name = "A",
                    Url = "http://example/api",
                    ApiKey = "k",
                    MaxResponseBytes = 0,
                },
            ],
        });
        Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems([
                new ConfigItem { ConfigName = ConfigKeys.IndexersInstances, ConfigValue = zero },
            ]));
    }

    [Fact]
    public void IndexerInstances_AcceptsExactHardMax()
    {
        var json = JsonSerializer.Serialize(new IndexerConfig
        {
            MaxResponseBytes = ExternalMetadataResponseLimits.HardMaxResponseBytes,
            Indexers = [],
        });
        ConfigManager.ValidateConfigItems([
            new ConfigItem { ConfigName = ConfigKeys.IndexersInstances, ConfigValue = json },
        ]);
    }
}
