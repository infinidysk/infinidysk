using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class ImportStrategyConfigTests
{
    [Theory]
    [InlineData("both")]
    [InlineData("invalid")]
    public void UnknownImportStrategy_IsRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
        [
            new ConfigItem { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = value },
        ]));
    }
}
