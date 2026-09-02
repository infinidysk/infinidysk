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
public sealed class SetupWizardServiceTests : IDisposable
{
    private readonly string? _previousApiKey =
        Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");

    public SetupWizardServiceTests()
    {
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "setup-wizard-test-key");
    }

    [Fact]
    public async Task GetStateAsync_TracksPendingResolvedAndStaleVersions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new DavDatabaseContext(options);
        await context.Database.EnsureCreatedAsync();
        var service = CreateService(context, out _);

        var pending = await service.GetStateAsync();
        Assert.True(pending.SetupRequired);
        Assert.Null(pending.RecordedVersion);

        context.SetupWizardStates.Add(new SetupWizardState
        {
            WizardVersion = SetupWizardService.CurrentWizardVersion,
            Disposition = SetupWizardDisposition.Completed,
            IngestionMethods = "[\"arrs\",\"manual\"]",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var completed = await service.GetStateAsync();
        Assert.False(completed.SetupRequired);
        Assert.Equal(SetupWizardDisposition.Completed, completed.Disposition);
        Assert.Equal(["arrs", "manual"], completed.IngestionMethods);

        var state = await context.SetupWizardStates.SingleAsync();
        state.WizardVersion = SetupWizardService.CurrentWizardVersion - 1;
        await context.SaveChangesAsync();

        var stale = await service.GetStateAsync();
        Assert.True(stale.SetupRequired);
    }

    [Fact]
    public async Task CompleteAsync_SymlinksAlwaysDisablesSegmentCache()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new DavDatabaseContext(options);
        await context.Database.EnsureCreatedAsync();
        var service = CreateService(context, out var configManager);

        var result = await service.CompleteAsync(new CompleteSetupWizardCommand
        {
            Strategy = "symlinks",
            IngestionMethods = ["manual"],
            ConfigItems =
            [
                new ConfigItem
                {
                    ConfigName = ConfigKeys.UsenetSegmentCacheEnabled,
                    ConfigValue = "true",
                },
            ],
        });

        var persisted = await context.ConfigItems.ToDictionaryAsync(
            item => item.ConfigName,
            item => item.ConfigValue);
        Assert.Equal("symlinks", persisted[ConfigKeys.ApiImportStrategy]);
        Assert.Equal("false", persisted[ConfigKeys.UsenetSegmentCacheEnabled]);
        Assert.False(configManager.IsSegmentCacheEnabled());
        Assert.True(result.RestartRequired);

        var state = await context.SetupWizardStates.SingleAsync();
        Assert.Equal(SetupWizardDisposition.Completed, state.Disposition);
    }

    [Fact]
    public async Task CompleteAsync_StrmAlwaysEnablesSegmentCache()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new DavDatabaseContext(options);
        await context.Database.EnsureCreatedAsync();
        var service = CreateService(context, out var configManager);
        configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetSegmentCacheEnabled,
                ConfigValue = "false",
            },
        ]);

        var result = await service.CompleteAsync(new CompleteSetupWizardCommand
        {
            Strategy = "strm",
            IngestionMethods = ["search"],
            ConfigItems =
            [
                new ConfigItem
                {
                    ConfigName = ConfigKeys.UsenetSegmentCacheEnabled,
                    ConfigValue = "false",
                },
            ],
        });

        Assert.True(configManager.IsSegmentCacheEnabled());
        Assert.True(result.RestartRequired);
        Assert.Equal(
            "true",
            await context.ConfigItems
                .Where(item => item.ConfigName == ConfigKeys.UsenetSegmentCacheEnabled)
                .Select(item => item.ConfigValue)
                .SingleAsync());
    }

    [Fact]
    public async Task SkipAsync_IsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new DavDatabaseContext(options);
        await context.Database.EnsureCreatedAsync();
        var service = CreateService(context, out _);

        await service.SkipAsync();
        await service.SkipAsync();

        var state = await context.SetupWizardStates.SingleAsync();
        Assert.Equal(SetupWizardDisposition.Skipped, state.Disposition);
        Assert.Equal(SetupWizardService.CurrentWizardVersion, state.WizardVersion);
    }

    [Fact]
    public async Task CompleteAsync_ManagedCacheConflictDoesNotResolveSetup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new DavDatabaseContext(options);
        await context.Database.EnsureCreatedAsync();
        var service = CreateService(context, out var configManager);
        configManager.ApplyEnvironmentOverlay(
            ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
            {
                ["NZBDAV_CONFIG__USENET__SEGMENT_CACHE__ENABLED"] = "true",
            }));

        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            service.CompleteAsync(new CompleteSetupWizardCommand
            {
                Strategy = "symlinks",
                IngestionMethods = ["manual"],
                ConfigItems = [],
            }));

        Assert.Contains("NZBDAV_CONFIG__USENET__SEGMENT_CACHE__ENABLED", error.Message);
        Assert.Empty(context.SetupWizardStates);
        Assert.Empty(context.ConfigItems);
    }

    [Fact]
    public async Task CompleteAsync_UnsupportedConfigDoesNotPartiallyPersist()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new DavDatabaseContext(options);
        await context.Database.EnsureCreatedAsync();
        var service = CreateService(context, out _);

        await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            service.CompleteAsync(new CompleteSetupWizardCommand
            {
                Strategy = "symlinks",
                IngestionMethods = ["manual"],
                ConfigItems =
                [
                    new ConfigItem
                    {
                        ConfigName = ConfigKeys.WebdavUser,
                        ConfigValue = "unexpected",
                    },
                ],
            }));

        Assert.Empty(context.SetupWizardStates);
        Assert.Empty(context.ConfigItems);
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", _previousApiKey);

    private static SetupWizardService CreateService(
        DavDatabaseContext context,
        out ConfigManager configManager)
    {
        var dbClient = new DavDatabaseClient(context);
        configManager = new ConfigManager();
        var configUpdateService = new ConfigUpdateService(dbClient, configManager);
        return new SetupWizardService(dbClient, configManager, configUpdateService);
    }
}