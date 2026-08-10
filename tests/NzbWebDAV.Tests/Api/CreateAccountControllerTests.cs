using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.CreateAccount;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(ConfigPathCollection))]
public sealed class CreateAccountControllerTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-createaccount-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(_options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task CreateAccountAsync_FirstAdmin_Succeeds()
    {
        var controller = CreateController();
        var response = await Invoke(controller, "alice", "hunter2", "Admin");

        Assert.True(response.Status);
        Assert.Equal(1, await _context.Accounts.CountAsync(a => a.Type == Account.AccountType.Admin));
    }

    [Fact]
    public async Task CreateAccountAsync_SecondAdmin_RejectedByPreCheck()
    {
        var first = CreateController();
        await Invoke(first, "alice", "hunter2", "Admin");

        var second = CreateController();
        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => Invoke(second, "mallory", "letmein", "Admin"));

        Assert.Contains("admin account already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await _context.Accounts.CountAsync(a => a.Type == Account.AccountType.Admin));
    }

    [Fact]
    public async Task CreateAccountAsync_ConcurrentOnboarding_OnlyOneAdminSurvivesAtTheDatabaseLevel()
    {
        // Simulates the actual TOCTOU: two requests both observe "no admin yet" (via
        // separate DbContexts, same as two real concurrent HTTP requests would each get
        // their own scoped context) and both attempt the insert. The unique filtered index
        // is the authoritative backstop that must reject the loser regardless of what any
        // application-level pre-check saw.
        await using var raceCtx = new DavDatabaseContext(_options);
        raceCtx.Accounts.Add(new Account
        {
            Type = Account.AccountType.Admin,
            Username = "mallory",
            RandomSalt = "s",
            PasswordHash = "h",
        });

        _context.Accounts.Add(new Account
        {
            Type = Account.AccountType.Admin,
            Username = "alice",
            RandomSalt = "s",
            PasswordHash = "h",
        });

        await raceCtx.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
        Assert.True(CreateAccountController.IsSingleAdminUniqueViolation(ex));

        Assert.Equal(1, await raceCtx.Accounts.CountAsync(a => a.Type == Account.AccountType.Admin));
    }

    [Fact]
    public async Task CreateAccountAsync_SecondWebDavAccount_StillAllowed()
    {
        // The unique index only restricts Type == Admin - unrelated WebDav accounts
        // (multiple of which are a supported, intentional feature) must be unaffected.
        var controller = CreateController();
        await Invoke(controller, "alice", "hunter2", "WebDav");
        var response = await Invoke(CreateController(), "bob", "swordfish", "WebDav");

        Assert.True(response.Status);
        Assert.Equal(2, await _context.Accounts.CountAsync(a => a.Type == Account.AccountType.WebDav));
    }

    [Fact]
    public async Task CreateAccountAsync_DuplicateWebDavUsername_ReturnsFriendlyBadRequest()
    {
        await Invoke(CreateController(), "alice", "hunter2", "WebDav");

        await using var duplicateContext = new DavDatabaseContext(_options);
        var duplicateController = new CreateAccountController(new DavDatabaseClient(duplicateContext));
        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => Invoke(duplicateController, "alice", "swordfish", "WebDav"));

        Assert.Contains("account with that username already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await duplicateContext.Accounts
            .Where(a => a.Type == Account.AccountType.WebDav)
            .ToListAsync());
    }

    [Fact]
    public async Task SingleAdminMigration_ExistingDuplicates_KeepsEarliestAdmin()
    {
        var migrationDbPath = Path.Join(_configRoot, "duplicate-admin-migration.sqlite");
        var migrationOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={migrationDbPath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;

        await using var migrationContext = new DavDatabaseContext(migrationOptions);
        var migrator = migrationContext.GetService<IMigrator>();
        await migrator.MigrateAsync("20260718000000_Add-NzbResolutionGroups-Table");
        await migrationContext.Database.ExecuteSqlRawAsync("""
            INSERT INTO Accounts (Type, Username, PasswordHash, RandomSalt)
            VALUES (1, 'alice', 'hash', 'salt');
            INSERT INTO Accounts (Type, Username, PasswordHash, RandomSalt)
            VALUES (1, 'mallory', 'hash', 'salt');
            """);

        await migrator.MigrateAsync();

        var admin = await migrationContext.Accounts
            .SingleAsync(a => a.Type == Account.AccountType.Admin);
        Assert.Equal("alice", admin.Username);
    }

    private CreateAccountController CreateController() => new(_dbClient);

    private static Task<CreateAccountResponse> Invoke(
        CreateAccountController controller, string username, string password, string type)
    {
        var httpContext = new DefaultHttpContext
        {
            Request =
            {
                Form = new FormCollection(new Dictionary<string, StringValues>
                {
                    ["username"] = username,
                    ["password"] = password,
                    ["type"] = type,
                }),
            },
        };
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = httpContext,
        };
        var request = new CreateAccountRequest(httpContext);
        return controller.CreateAccount(request);
    }
}
