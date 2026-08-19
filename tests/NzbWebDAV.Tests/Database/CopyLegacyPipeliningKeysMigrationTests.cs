using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

public sealed class CopyLegacyPipeliningKeysMigrationTests
{
    private const string PriorMigration = "20260818200000_Add-ArticleMiss-Cache";

    [Fact]
    public async Task CopyLegacyPipeliningKeys_CopiesWhenMissingAndPreservesLegacyRows()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        ctx.ConfigItems.AddRange(
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetPipeliningEnabled,
                ConfigValue = "true",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetPipeliningDepth,
                ConfigValue = "12",
            });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        var legacyEnabled = await ctx.ConfigItems.AsNoTracking()
            .SingleAsync(x => x.ConfigName == ConfigKeys.UsenetPipeliningEnabled);
        var legacyDepth = await ctx.ConfigItems.AsNoTracking()
            .SingleAsync(x => x.ConfigName == ConfigKeys.UsenetPipeliningDepth);
        var newEnabled = await ctx.ConfigItems.AsNoTracking()
            .SingleAsync(x => x.ConfigName == ConfigKeys.UsenetQueuePipeliningEnabled);
        var newDepth = await ctx.ConfigItems.AsNoTracking()
            .SingleAsync(x => x.ConfigName == ConfigKeys.UsenetQueuePipeliningDepth);

        Assert.Equal("true", legacyEnabled.ConfigValue);
        Assert.Equal("12", legacyDepth.ConfigValue);
        Assert.Equal("true", newEnabled.ConfigValue);
        Assert.Equal("12", newDepth.ConfigValue);
    }

    [Fact]
    public async Task CopyLegacyPipeliningKeys_DoesNotOverwriteExistingNewRows()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        ctx.ConfigItems.AddRange(
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetPipeliningEnabled,
                ConfigValue = "true",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetQueuePipeliningEnabled,
                ConfigValue = "false",
            });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        var newEnabled = await ctx.ConfigItems.AsNoTracking()
            .SingleAsync(x => x.ConfigName == ConfigKeys.UsenetQueuePipeliningEnabled);
        Assert.Equal("false", newEnabled.ConfigValue);
    }

    [Fact]
    public async Task CopyLegacyPipeliningKeys_IsIdempotentOnReRun()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        ctx.ConfigItems.Add(new ConfigItem
        {
            ConfigName = ConfigKeys.UsenetPipeliningEnabled,
            ConfigValue = "true",
        });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await ctx.Database.MigrateAsync();
        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        Assert.Equal(1, await ctx.ConfigItems.AsNoTracking()
            .CountAsync(x => x.ConfigName == ConfigKeys.UsenetQueuePipeliningEnabled));
    }

    private sealed class MigrationHarness : IAsyncDisposable
    {
        private readonly string _databasePath;

        private MigrationHarness(string databasePath, DavDatabaseContext context)
        {
            _databasePath = databasePath;
            Context = context;
        }

        public DavDatabaseContext Context { get; }

        public static async Task<MigrationHarness> CreateAsync()
        {
            var databasePath = Path.Join(Path.GetTempPath(), $"nzbdav-pipe-mig-{Guid.NewGuid():N}.sqlite");
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={databasePath}")
                .AddInterceptors(new SqliteForeignKeyEnabler())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            var context = new DavDatabaseContext(options);
            await context.Database.MigrateAsync(PriorMigration);
            return new MigrationHarness(databasePath, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            File.Delete(_databasePath);
        }
    }
}
