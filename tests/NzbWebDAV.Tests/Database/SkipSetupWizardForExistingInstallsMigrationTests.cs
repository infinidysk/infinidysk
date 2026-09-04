using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

public sealed class SkipSetupWizardForExistingInstallsMigrationTests
{
    private const string PriorMigration =
        "20260903120000_Pin-Segment-Cache-For-Existing-Installs";

    [Fact]
    public async Task ExistingInstallWithAccount_IsSkipped()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var context = harness.Context;

        context.Accounts.Add(new Account
        {
            Type = Account.AccountType.Admin,
            Username = "admin",
            PasswordHash = "hash",
            RandomSalt = "salt",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();

        var state = await context.SetupWizardStates.AsNoTracking().SingleAsync();
        Assert.Equal(1, state.WizardVersion);
        Assert.Equal(SetupWizardDisposition.Skipped, state.Disposition);
    }

    [Fact]
    public async Task ExistingAuthDisabledInstallWithProvider_IsSkipped()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var context = harness.Context;

        context.ConfigItems.Add(new ConfigItem
        {
            ConfigName = ConfigKeys.UsenetProviders,
            ConfigValue = "{\"Providers\":[]}",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();

        Assert.Equal(
            SetupWizardDisposition.Skipped,
            (await context.SetupWizardStates.AsNoTracking().SingleAsync()).Disposition);
    }

    [Fact]
    public async Task ExistingWizardState_IsNotOverwritten()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var context = harness.Context;

        context.Accounts.Add(new Account
        {
            Type = Account.AccountType.Admin,
            Username = "admin",
            PasswordHash = "hash",
            RandomSalt = "salt",
        });
        context.SetupWizardStates.Add(new SetupWizardState
        {
            WizardVersion = 1,
            Disposition = SetupWizardDisposition.Completed,
            IngestionMethods = "[\"manual\"]",
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(1),
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();

        var state = await context.SetupWizardStates.AsNoTracking().SingleAsync();
        Assert.Equal(SetupWizardDisposition.Completed, state.Disposition);
        Assert.Equal("[\"manual\"]", state.IngestionMethods);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1), state.UpdatedAt);
    }

    [Fact]
    public async Task FreshInstall_RemainsPending()
    {
        await using var harness = await MigrationHarness.CreateAsync();
        var context = harness.Context;

        var accountCount = await context.Accounts.CountAsync();
        var providerCount = await context.ConfigItems.CountAsync(
            item => item.ConfigName == ConfigKeys.UsenetProviders);
        var davItemCount = await context.Items.CountAsync();
        var queueCount = await context.QueueItems.CountAsync();
        var historyCount = await context.HistoryItems.CountAsync();
        Assert.False(
            accountCount > 0 || providerCount > 0 || davItemCount > 6 ||
            queueCount > 0 || historyCount > 0,
            $"accounts={accountCount}, providers={providerCount}, dav={davItemCount}, " +
            $"queue={queueCount}, history={historyCount}");

        await context.Database.MigrateAsync();
        context.ChangeTracker.Clear();

        Assert.Empty(await context.SetupWizardStates.AsNoTracking().ToListAsync());
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
            var databasePath = Path.Join(
                Path.GetTempPath(),
                $"nzbdav-setup-skip-mig-{Guid.NewGuid():N}.sqlite");
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
