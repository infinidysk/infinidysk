using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
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

    [Fact]
    public void MaxEntries_ConcurrentMarksPastCap_DoesNotThrow()
    {
        var config = CreateConfig(ttlSeconds: 300, maxEntries: 100);
        var cache = new ArticleMissNegativeCache(config);

        Parallel.For(0, 8_000, i =>
        {
            cache.MarkMissing(ArticleMissNegativeCache.BuildKey($"art-{i}", "p", null));
        });

        // Cleanup re-checks the live count after releasing the single-flight, so once
        // the parallel marks join, the last cleaner has trimmed to the cap without
        // needing a final sequential mark.
        Assert.True(cache.Entries <= 100);
        Assert.True(cache.Entries > 0);
    }

    [Fact]
    public async Task MaxEntries_RoundCapReachedOverCap_SchedulesCoalescedContinuation()
    {
        var config = CreateConfig(ttlSeconds: 300, maxEntries: 100);
        var cache = new ArticleMissNegativeCache(config);

        for (var i = 0; i < 100; i++)
        {
            cache.MarkMissingAtForTests(
                ArticleMissNegativeCache.BuildKey($"seed-{i}", "p", null),
                DateTimeOffset.UtcNow.AddMilliseconds(i));
        }

        // Every round lands a fresh mark after that round's snapshot (the hook runs
        // while the single-flight is held, so the nested mark skips cleanup), so all
        // eight rounds finish over cap and the round cap is reached.
        var sabotage = 1;
        var lateMarks = 0;
        cache.CleanupRoundCompletedForTests = () =>
        {
            if (Volatile.Read(ref sabotage) == 0) return;
            var n = Interlocked.Increment(ref lateMarks);
            cache.MarkMissingAtForTests(
                ArticleMissNegativeCache.BuildKey($"late-{n}", "p", null),
                DateTimeOffset.UtcNow);
        };

        // The 101st mark pushes over the cap and runs the capped cleanup loop; the
        // hook keeps every round over cap, so all eight rounds fire synchronously.
        cache.MarkMissing(ArticleMissNegativeCache.BuildKey("trigger", "p", null));
        Assert.True(Volatile.Read(ref lateMarks) >= 8);

        // Once marks stop arriving, the scheduled continuation must drain the cache
        // to the cap without any further MarkMissing call.
        Interlocked.Exchange(ref sabotage, 0);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (cache.Entries > 100 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(cache.Entries <= 100, $"continuation left {cache.Entries} entries");
        Assert.True(cache.Entries > 0);
    }

    [Fact]
    public async Task PersistentCache_HydratesFreshMisses_AndPurgesExpiredRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new DavDatabaseContext(options))
            await context.Database.EnsureCreatedAsync();

        var config = CreateConfig(ttlSeconds: 30, maxEntries: 100);
        var key = ArticleMissNegativeCache.BuildKey("segment", "a.example", null);
        using (var first = new ArticleMissNegativeCache(config, () => new DavDatabaseContext(options)))
        {
            await first.StartAsync(CancellationToken.None);
            await first.MarkMissingAndPersistForTestsAsync(key);
        }

        using var restarted = new ArticleMissNegativeCache(config, () => new DavDatabaseContext(options));
        await restarted.StartAsync(CancellationToken.None);
        Assert.True(restarted.IsMissing(key));

        await using (var context = new DavDatabaseContext(options))
        {
            var persisted = await context.ArticleMissCacheEntries.SingleAsync();
            persisted.ConfirmedAtUnix = DateTimeOffset.UtcNow.AddSeconds(-31).ToUnixTimeMilliseconds();
            await context.SaveChangesAsync();
        }

        using var afterExpiry = new ArticleMissNegativeCache(config, () => new DavDatabaseContext(options));
        await afterExpiry.StartAsync(CancellationToken.None);
        Assert.False(afterExpiry.IsMissing(key));
        await using var verify = new DavDatabaseContext(options);
        Assert.Empty(await verify.ArticleMissCacheEntries.ToListAsync());
    }

    [Fact]
    public async Task PersistentCache_Hydration_EvictsOldestRowsBeyondCap()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new DavDatabaseContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 101; i++)
            {
                context.ArticleMissCacheEntries.Add(new ArticleMissCacheEntry
                {
                    CacheKey = $"segment-{i}\u0001p:provider",
                    ConfirmedAtUnix = now.AddMilliseconds(i).ToUnixTimeMilliseconds(),
                });
            }
            await context.SaveChangesAsync();
        }

        var config = CreateConfig(ttlSeconds: 300, maxEntries: 100);
        using var cache = new ArticleMissNegativeCache(config, () => new DavDatabaseContext(options));
        await cache.StartAsync(CancellationToken.None);

        Assert.Equal(100, cache.Entries);
        Assert.False(cache.IsMissing("segment-0\u0001p:provider"));
        Assert.True(cache.IsMissing("segment-100\u0001p:provider"));
        await using var verify = new DavDatabaseContext(options);
        Assert.Equal(100, await verify.ArticleMissCacheEntries.CountAsync());
    }

    [Fact]
    public async Task MarkMissing_PersistsAsynchronously_AndStopAsyncDrainsQueue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new DavDatabaseContext(options))
            await context.Database.EnsureCreatedAsync();

        var config = CreateConfig(ttlSeconds: 300, maxEntries: 100);
        var key = ArticleMissNegativeCache.BuildKey("segment", "a.example", null);
        using (var cache = new ArticleMissNegativeCache(config, () => new DavDatabaseContext(options)))
        {
            await cache.StartAsync(CancellationToken.None);
            cache.MarkMissing(key);
            await cache.StopAsync(CancellationToken.None);
        }

        await using var verify = new DavDatabaseContext(options);
        Assert.Equal(key, (await verify.ArticleMissCacheEntries.SingleAsync()).CacheKey);
    }

    [Fact]
    public async Task ProviderConfigChange_ClearsPersistedEntries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new DavDatabaseContext(options))
            await context.Database.EnsureCreatedAsync();

        var config = CreateConfig(ttlSeconds: 300, maxEntries: 100);
        using var cache = new ArticleMissNegativeCache(config, () => new DavDatabaseContext(options));
        await cache.StartAsync(CancellationToken.None);
        await cache.MarkMissingAndPersistForTestsAsync(
            ArticleMissNegativeCache.BuildKey("segment", "a.example", null));

        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = """{"Providers":[]}""",
            },
        ]);
        await cache.FlushPersistenceForTestsAsync();

        Assert.Equal(0, cache.Entries);
        await using var verify = new DavDatabaseContext(options);
        Assert.Empty(await verify.ArticleMissCacheEntries.ToListAsync());
    }

    [Fact]
    public async Task PersistedTrim_EnforcesMaxEntries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new DavDatabaseContext(options))
            await context.Database.EnsureCreatedAsync();

        var config = CreateConfig(ttlSeconds: 300, maxEntries: 100);
        using var cache = new ArticleMissNegativeCache(config, () => new DavDatabaseContext(options));
        await cache.StartAsync(CancellationToken.None);
        for (var i = 0; i < 150; i++)
            cache.MarkMissing(ArticleMissNegativeCache.BuildKey($"art-{i}", "p", null));
        await cache.FlushPersistenceForTestsAsync();

        await using var verify = new DavDatabaseContext(options);
        Assert.Equal(100, await verify.ArticleMissCacheEntries.CountAsync());
        Assert.Null(await verify.ArticleMissCacheEntries
            .FindAsync(ArticleMissNegativeCache.BuildKey("art-0", "p", null)));
        Assert.NotNull(await verify.ArticleMissCacheEntries
            .FindAsync(ArticleMissNegativeCache.BuildKey("art-149", "p", null)));
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
