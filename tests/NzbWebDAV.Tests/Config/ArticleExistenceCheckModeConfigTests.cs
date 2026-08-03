using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class ArticleExistenceCheckModeConfigTests
{
    [Theory]
    [InlineData("full", "full")]
    [InlineData("SAMPLED", "sampled")]
    public void ConfiguredMode_IsValidatedAndResolved(string configured, string expected)
    {
        var item = new ConfigItem
        {
            ConfigName = ConfigKeys.ApiArticleExistenceCheckMode,
            ConfigValue = configured,
        };
        ConfigManager.ValidateConfigItems([item]);
        var config = new ConfigManager();
        config.UpdateValues([item]);

        Assert.Equal(expected, config.GetArticleExistenceCheckMode());
    }

    [Fact]
    public void UnknownMode_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiArticleExistenceCheckMode,
                ConfigValue = "partial",
            },
        ]));
    }
}
