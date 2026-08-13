using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class RetrieveUserAgentConfigTests
{
    [Fact]
    public void GetUserAgent_DefaultsToSabnzbdWhenUnset()
    {
        var previous = Environment.GetEnvironmentVariable("NZB_GRAB_USER_AGENT");
        try
        {
            Environment.SetEnvironmentVariable("NZB_GRAB_USER_AGENT", null);
            var config = new ConfigManager();
            Assert.Equal("SABnzbd/5.1.0", config.GetUserAgent());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NZB_GRAB_USER_AGENT", previous);
        }
    }

    [Fact]
    public void GetUserAgent_UsesSavedConfigValue()
    {
        var previous = Environment.GetEnvironmentVariable("NZB_GRAB_USER_AGENT");
        try
        {
            Environment.SetEnvironmentVariable("NZB_GRAB_USER_AGENT", "env-agent");
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.ApiUserAgent, ConfigValue = "custom-agent" },
            ]);
            Assert.Equal("custom-agent", config.GetUserAgent());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NZB_GRAB_USER_AGENT", previous);
        }
    }

    [Fact]
    public void GetUserAgent_UsesEnvWhenConfigEmpty()
    {
        var previous = Environment.GetEnvironmentVariable("NZB_GRAB_USER_AGENT");
        try
        {
            Environment.SetEnvironmentVariable("NZB_GRAB_USER_AGENT", "env-agent");
            var config = new ConfigManager();
            Assert.Equal("env-agent", config.GetUserAgent());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NZB_GRAB_USER_AGENT", previous);
        }
    }
}
