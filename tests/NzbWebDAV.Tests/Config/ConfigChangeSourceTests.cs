using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class ConfigChangeSourceTests
{
    [Fact]
    public void Subscribe_DeliversUpdatesUntilDisposed()
    {
        var config = new ConfigManager();
        var hits = 0;
        using (config.Subscribe((_, _) => hits++))
        {
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://a" }]);
            Assert.Equal(1, hits);
        }

        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://b" }]);
        Assert.Equal(1, hits);
    }
}
