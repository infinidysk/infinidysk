using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class ProxyConfigTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void ValidBoolean_IsAccepted(string configured)
    {
        ConfigManager.ValidateConfigItems([
            new ConfigItem
            {
                ConfigName = ConfigKeys.GeneralTrustProxy,
                ConfigValue = configured,
            },
        ]);
    }

    [Fact]
    public void InvalidValue_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems([
            new ConfigItem
            {
                ConfigName = ConfigKeys.GeneralTrustProxy,
                ConfigValue = "sometimes",
            },
        ]));
    }

    [Theory]
    [InlineData("https://nzbdav.example.com")]
    [InlineData("http://localhost:3000/app")]
    public void ValidBaseUrl_IsAccepted(string configured)
    {
        ConfigManager.ValidateConfigItems([
            new ConfigItem
            {
                ConfigName = ConfigKeys.GeneralBaseUrl,
                ConfigValue = configured,
            },
        ]);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://nzbdav.example.com")]
    [InlineData("https://user@nzbdav.example.com")]
    [InlineData("https://nzbdav.example.com?query=true")]
    [InlineData("https://nzbdav.example.com#fragment")]
    public void InvalidBaseUrl_IsRejected(string configured)
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems([
            new ConfigItem
            {
                ConfigName = ConfigKeys.GeneralBaseUrl,
                ConfigValue = configured,
            },
        ]));
    }
}