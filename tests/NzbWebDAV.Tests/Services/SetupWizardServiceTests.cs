using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class SetupWizardServiceTests
{
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
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "setup-wizard-test-key");
        try
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
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

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