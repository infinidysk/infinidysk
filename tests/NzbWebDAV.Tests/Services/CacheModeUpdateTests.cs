using System.Collections;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class CacheModeUpdateTests : IDisposable
{
    private readonly string? _previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");

    public CacheModeUpdateTests() =>
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "cache-mode-tests");

    public void Dispose() => Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", _previousApiKey);

    [Fact]
    public async Task ConcurrentSaves_PreserveDatabaseAndRuntimeParity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        await using var firstContext = new DavDatabaseContext(options);
        await using var secondContext = new DavDatabaseContext(options);
        await firstContext.Database.EnsureCreatedAsync();
        var config = new ConfigManager();
        var firstService = new ConfigUpdateService(new DavDatabaseClient(firstContext), config);
        var secondService = new ConfigUpdateService(new DavDatabaseClient(secondContext), config);
        using var first = await firstService.StageAsync([
            new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = "segment" },
        ]);
        var secondSave = secondService.ApplyAsync([
            new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = "off" },
        ]);
        Assert.False(secondSave.IsCompleted);
        await firstContext.SaveChangesAsync();
        firstService.Publish(first);
        using var second = await secondSave.WaitAsync(TimeSpan.FromSeconds(5));
        var persisted = await secondContext.ConfigItems.AsNoTracking().ToDictionaryAsync(
            item => item.ConfigName, item => item.ConfigValue);
        Assert.Equal("off", persisted[ConfigKeys.CacheMode]);
        Assert.Equal("false", persisted[ConfigKeys.UsenetSegmentCacheEnabled]);
        Assert.Equal(CacheMode.Off, config.GetCacheMode());
        Assert.Equal(CacheMode.Off, config.GetActiveCacheMode());
    }

    [Fact]
    public async Task StagedBatch_HoldsWriteLeaseUntilPublish()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        await using var firstContext = new DavDatabaseContext(options);
        await using var secondContext = new DavDatabaseContext(options);
        await firstContext.Database.EnsureCreatedAsync();
        var config = new ConfigManager();
        var firstService = new ConfigUpdateService(new DavDatabaseClient(firstContext), config);
        var secondService = new ConfigUpdateService(new DavDatabaseClient(secondContext), config);
        using var batch = await firstService.StageAsync([
            new ConfigItem { ConfigName = "cache.mode", ConfigValue = "segment" },
        ]);
        try
        {
            using var cancel = new CancellationTokenSource();
            var second = secondService.StageAsync([
                new ConfigItem { ConfigName = "cache.mode", ConfigValue = "native" },
            ], cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.False(secondContext.ChangeTracker.HasChanges());
        }
        finally
        {
            await firstContext.SaveChangesAsync();
            firstService.Publish(batch);
        }
    }

    [Theory]
    [InlineData("true", "segment")]
    [InlineData("false", "off")]
    public async Task LegacyOnlyUpdate_MapsToMode(string legacy, string expectedMode)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem { ConfigName = "cache.mode", ConfigValue = "native" }]);
        var service = new ConfigUpdateService(new DavDatabaseClient(context), config);
        await service.ApplyAsync([
            new ConfigItem { ConfigName = ConfigKeys.UsenetSegmentCacheEnabled, ConfigValue = legacy },
        ]);
        Assert.Equal(expectedMode, config.GetEffectiveConfigValue("cache.mode"));
        Assert.Equal(expectedMode, (await context.ConfigItems.SingleAsync(item => item.ConfigName == "cache.mode")).ConfigValue);
    }

    [Theory]
    [InlineData("native", "true")]
    [InlineData("segment", "false")]
    public async Task ContradictoryBatch_RejectsBeforeTracking(string mode, string legacy)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        var service = new ConfigUpdateService(new DavDatabaseClient(context), new ConfigManager());
        await Assert.ThrowsAsync<BadHttpRequestException>(() => service.StageAsync([
            new ConfigItem { ConfigName = "cache.mode", ConfigValue = mode },
            new ConfigItem { ConfigName = ConfigKeys.UsenetSegmentCacheEnabled, ConfigValue = legacy },
        ]));
        Assert.False(context.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task LegacyUpdate_CannotOverrideEnvironmentMode()
    {
        var config = new ConfigManager();
        config.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__CACHE__MODE"] = "native",
        }));
        await using var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var service = new ConfigUpdateService(new DavDatabaseClient(context), config);
        await Assert.ThrowsAsync<BadHttpRequestException>(() => service.StageAsync([
            new ConfigItem { ConfigName = ConfigKeys.UsenetSegmentCacheEnabled, ConfigValue = "true" },
        ]));
        Assert.False(context.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ModeUpdate_CannotContradictEnvironmentLegacyFlag()
    {
        var config = new ConfigManager();
        config.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__USENET__SEGMENT_CACHE__ENABLED"] = "true",
        }));
        await using var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var service = new ConfigUpdateService(new DavDatabaseClient(context), config);
        await Assert.ThrowsAsync<BadHttpRequestException>(() => service.StageAsync([
            new ConfigItem { ConfigName = "cache.mode", ConfigValue = "native" },
        ]));
        Assert.False(context.ChangeTracker.HasChanges());
    }
}
