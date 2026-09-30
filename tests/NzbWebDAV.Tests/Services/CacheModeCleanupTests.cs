using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class CacheModeCleanupTests
{
    [Theory]
    [InlineData("native")]
    [InlineData("off")]
    public async Task ExplicitMode_RetainsInactiveSegmentCache(string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cache-mode-" + Guid.NewGuid().ToString("N"));
        var shard = Path.Combine(directory, "ab");
        Directory.CreateDirectory(shard);
        var file = Path.Combine(shard, new string('a', 64));
        await File.WriteAllTextAsync(file, "retained cached bytes");
        try
        {
            var config = new ConfigManager();
            config.UpdateValues([
                new ConfigItem { ConfigName = "cache.mode", ConfigValue = mode },
                new ConfigItem { ConfigName = ConfigKeys.UsenetSegmentCachePath, ConfigValue = directory },
            ]);
            using var service = new SegmentCacheCleanupService(config);
            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!;
            Assert.True(File.Exists(file));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
