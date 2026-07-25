using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ArticleMissNegativeCacheTests
{
    [Theory]
    [InlineData(null, 300)]
    [InlineData("", 300)]
    [InlineData("abc", 300)]
    [InlineData("10", 30)]
    [InlineData("30", 30)]
    [InlineData("300", 300)]
    [InlineData("86400", 86400)]
    [InlineData("999999", 86400)]
    public void GetArticleMissCacheTtl_ClampsAndFallsBack(string? value, int expectedSeconds)
    {
        var config = new ConfigManager();
        if (value is not null)
        {
            config.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = ConfigKeys.UsenetArticleMissCacheTtlSeconds,
                    ConfigValue = value,
                },
            ]);
        }

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), config.GetArticleMissCacheTtl());
    }

    [Theory]
    [InlineData(null, 10_000)]
    [InlineData("", 10_000)]
    [InlineData("abc", 10_000)]
    [InlineData("50", 100)]
    [InlineData("100", 100)]
    [InlineData("10000", 10_000)]
    [InlineData("1000000", 1_000_000)]
    [InlineData("2000000", 1_000_000)]
    public void GetArticleMissCacheMaxEntries_ClampsAndFallsBack(string? value, int expected)
    {
        var config = new ConfigManager();
        if (value is not null)
        {
            config.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = ConfigKeys.UsenetArticleMissCacheMaxEntries,
                    ConfigValue = value,
                },
            ]);
        }

        Assert.Equal(expected, config.GetArticleMissCacheMaxEntries());
    }

    [Fact]
    public void BuildKey_UsesStorageGroupWhenPresent()
    {
        Assert.Equal(
            "article\u0001g:omicron",
            ArticleMissNegativeCache.BuildKey("article", "host-a", "omicron"));
        Assert.Equal(
            "article\u0001p:host-a",
            ArticleMissNegativeCache.BuildKey("article", "host-a", null));
        Assert.Equal(
            "article\u0001p:host-a",
            ArticleMissNegativeCache.BuildKey("article", "host-a", "  "));
    }

    [Fact]
    public void IsMissing_ReturnsTrueWithinTtl_AndFalseAfterExpiry()
    {
        var config = CreateConfig(ttlSeconds: 300, maxEntries: 1000);
        var cache = new ArticleMissNegativeCache(config);
        var key = ArticleMissNegativeCache.BuildKey("seg", "a", null);

        Assert.False(cache.IsMissing(key));
        cache.MarkMissing(key);
        Assert.True(cache.IsMissing(key));
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Skips);
        Assert.Equal(1, cache.Entries);

        cache.MarkMissingAtForTests(key, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(301));
        Assert.False(cache.IsMissing(key));
        Assert.Equal(0, cache.Entries);
    }

    [Fact]
    public void ProviderConfigChange_ClearsCache()
    {
        var config = CreateConfig(ttlSeconds: 300, maxEntries: 1000);
        var cache = new ArticleMissNegativeCache(config);
        cache.MarkMissing(ArticleMissNegativeCache.BuildKey("seg", "a", null));
        Assert.Equal(1, cache.Entries);

        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = """{"Providers":[]}""",
            },
        ]);

        Assert.Equal(0, cache.Entries);
        Assert.False(cache.IsMissing(ArticleMissNegativeCache.BuildKey("seg", "a", null)));
    }

    [Fact]
    public void MaxEntries_EvictsOldestWhenOverCap()
    {
        var config = CreateConfig(ttlSeconds: 300, maxEntries: 100);
        var cache = new ArticleMissNegativeCache(config);

        for (var i = 0; i < 150; i++)
        {
            cache.MarkMissingAtForTests(
                ArticleMissNegativeCache.BuildKey($"art-{i}", "p", null),
                DateTimeOffset.UtcNow.AddMilliseconds(i));
        }

        Assert.True(cache.Entries <= 100);
        Assert.False(cache.IsMissing(ArticleMissNegativeCache.BuildKey("art-0", "p", null)));
        Assert.True(cache.IsMissing(ArticleMissNegativeCache.BuildKey("art-149", "p", null)));
    }

    private static ConfigManager CreateConfig(int ttlSeconds, int maxEntries)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetArticleMissCacheTtlSeconds,
                ConfigValue = ttlSeconds.ToString(),
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetArticleMissCacheMaxEntries,
                ConfigValue = maxEntries.ToString(),
            },
        ]);
        return config;
    }
}
