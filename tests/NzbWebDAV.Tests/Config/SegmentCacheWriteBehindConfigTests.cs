using System.Collections;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class SegmentCacheWriteBehindConfigTests
{
    [Fact]
    public void Default_PreservesInlineWrites()
    {
        var config = new ConfigManager();

        Assert.Equal(0, config.GetSegmentCacheWriteBehindMb());
        Assert.Equal(0, config.GetSegmentCacheWriteBehindBytes());
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("16", 16)]
    [InlineData("64", 64)]
    [InlineData("1024", 1024)]
    public void ValidValues_AreReturnedInMiB(string configured, int expected)
    {
        var config = new ConfigManager();
        var item = new ConfigItem
        {
            ConfigName = ConfigKeys.UsenetSegmentCacheWriteBehindMb,
            ConfigValue = configured,
        };

        ConfigManager.ValidateConfigItems([item]);
        config.UpdateValues([item]);
        Assert.Equal(expected, config.GetSegmentCacheWriteBehindMb());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1")]
    [InlineData("15")]
    [InlineData("1025")]
    [InlineData("invalid")]
    public void Validation_RejectsUnsupportedValues(string configured)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetSegmentCacheWriteBehindMb,
                ConfigValue = configured,
            },
        ]));
    }

    [Fact]
    public void EnvironmentOverlay_IsAuthoritative()
    {
        var config = new ConfigManager();
        config.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__USENET__SEGMENT_CACHE__WRITE_BEHIND_MB"] = "64",
        }));

        Assert.Equal(64, config.GetSegmentCacheWriteBehindMb());
        Assert.True(config.IsEnvironmentManaged(ConfigKeys.UsenetSegmentCacheWriteBehindMb));
    }
}
