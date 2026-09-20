using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Config;

public sealed class NativeCacheConfigTests
{
    [Fact]
    public void ConfigValidation_RejectsOverlappingNativeRoots()
    {
        var first = new NativeCacheFolder { Id = "one", Path = "/native-cache/media" };
        var second = first with { Id = "two", Path = "/native-cache/media/nested" };
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems([
            new ConfigItem { ConfigName = "cache.native.folders", ConfigValue = JsonSerializer.Serialize(new[] { first, second }) }
        ]));
    }

    [Fact]
    public void ConfigValidation_RejectsUnboundedNativeBufferBudget()
    {
        Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems([
            new ConfigItem { ConfigName = "cache.native.writer-mb", ConfigValue = "1000000" }
        ]));
    }
}
