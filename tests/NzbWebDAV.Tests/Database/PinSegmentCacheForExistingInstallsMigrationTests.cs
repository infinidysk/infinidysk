using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

public sealed class PinSegmentCacheForExistingInstallsMigrationTests
{
    private const string PriorMigration = "20260902000809_Add-Setup-Wizard-State";

    [Fact]
    public async Task ExistingInstallWithoutExplicitSetting_IsPinnedOn()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        ctx.ConfigItems.Add(new ConfigItem
        {
            ConfigName = ConfigKeys.UsenetProviders,
            ConfigValue = "{\"Providers\":[]}",
        });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        var pinned = await ctx.ConfigItems.AsNoTracking()
            .SingleAsync(x => x.ConfigName == ConfigKeys.UsenetSegmentCacheEnabled);
        Assert.Equal("true", pinned.ConfigValue);
    }

    [Fact]
    public async Task ExistingInstallWithExplicitSetting_IsLeftUnchanged()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        ctx.ConfigItems.AddRange(
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = "{\"Providers\":[]}",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetSegmentCacheEnabled,
                ConfigValue = "false",
            });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        var existing = await ctx.ConfigItems.AsNoTracking()
            .SingleAsync(x => x.ConfigName == ConfigKeys.UsenetSegmentCacheEnabled);
        Assert.Equal("false", existing.ConfigValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [MemberData(nameof(AllDotNetWhitespace))]
    public async Task ExistingInstallWithBlankSetting_IsPinnedOn(string blank)
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        ctx.ConfigItems.AddRange(
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = "{\"Providers\":[]}",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetSegmentCacheEnabled,
                ConfigValue = blank,
            });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        var pinned = await ctx.ConfigItems.AsNoTracking()
            .SingleAsync(x => x.ConfigName == ConfigKeys.UsenetSegmentCacheEnabled);
        Assert.Equal("true", pinned.ConfigValue);
    }

    public static TheoryData<string> AllDotNetWhitespace =>
    [
        new string(Enumerable.Range(char.MinValue, char.MaxValue + 1)
            .Select(value => (char)value)
            .Where(char.IsWhiteSpace)
            .ToArray()),
    ];

    [Fact]
    public async Task FreshInstall_IsNotPinned_AndDefaultsOff()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var ctx = harness.Context;

        await ctx.Database.MigrateAsync();
        ctx.ChangeTracker.Clear();

        Assert.False(await ctx.ConfigItems.AsNoTracking()
            .AnyAsync(x => x.ConfigName == ConfigKeys.UsenetSegmentCacheEnabled));

        var config = new ConfigManager();
        config.UpdateValues(await ctx.ConfigItems.AsNoTracking().ToListAsync());
        Assert.False(config.IsSegmentCacheEnabled());
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
            var databasePath = Path.Join(Path.GetTempPath(), $"nzbdav-segcache-mig-{Guid.NewGuid():N}.sqlite");
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
