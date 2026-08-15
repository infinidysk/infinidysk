using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tasks;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Tasks;

[Collection(nameof(ConfigPathCollection))]
public sealed class ResetAdminAccountTaskTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-resetadmin-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private string? _previousResetEnv;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        _previousResetEnv = Environment.GetEnvironmentVariable(ResetAdminAccountTask.EnvVarName);
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
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        Environment.SetEnvironmentVariable(ResetAdminAccountTask.EnvVarName, _previousResetEnv);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("y")]
    [InlineData(" true ")]
    public async Task RunIfRequestedAsync_DeletesAdminAccount_WhenEnvVarIsTruthy(string envValue)
    {
        Environment.SetEnvironmentVariable(ResetAdminAccountTask.EnvVarName, envValue);
        await SeedAccountsAsync();

        await ResetAdminAccountTask.RunIfRequestedAsync(_context);

        Assert.Equal(0, await _context.Accounts.CountAsync(a => a.Type == Account.AccountType.Admin));
        Assert.Equal(1, await _context.Accounts.CountAsync(a => a.Type == Account.AccountType.WebDav));
    }

    [Fact]
    public async Task RunIfRequestedAsync_NoOp_WhenEnvVarUnset()
    {
        Environment.SetEnvironmentVariable(ResetAdminAccountTask.EnvVarName, null);
        await SeedAccountsAsync();

        await ResetAdminAccountTask.RunIfRequestedAsync(_context);

        Assert.Equal(1, await _context.Accounts.CountAsync(a => a.Type == Account.AccountType.Admin));
        Assert.Equal(1, await _context.Accounts.CountAsync(a => a.Type == Account.AccountType.WebDav));
    }

    [Fact]
    public async Task RunIfRequestedAsync_NoOp_WhenEnvVarInvalid()
    {
        Environment.SetEnvironmentVariable(ResetAdminAccountTask.EnvVarName, "maybe");
        await SeedAccountsAsync();

        await ResetAdminAccountTask.RunIfRequestedAsync(_context);

        Assert.Equal(1, await _context.Accounts.CountAsync(a => a.Type == Account.AccountType.Admin));
    }

    [Fact]
    public async Task RunIfRequestedAsync_NoOp_WhenAdminMissing()
    {
        Environment.SetEnvironmentVariable(ResetAdminAccountTask.EnvVarName, "true");
        _context.Accounts.Add(new Account
        {
            Type = Account.AccountType.WebDav,
            Username = "webdav-user",
            PasswordHash = "hash",
            RandomSalt = "salt",
        });
        await _context.SaveChangesAsync();

        await ResetAdminAccountTask.RunIfRequestedAsync(_context);

        Assert.Equal(0, await _context.Accounts.CountAsync(a => a.Type == Account.AccountType.Admin));
        Assert.Equal(1, await _context.Accounts.CountAsync(a => a.Type == Account.AccountType.WebDav));
    }

    private async Task SeedAccountsAsync()
    {
        _context.Accounts.AddRange(
            new Account
            {
                Type = Account.AccountType.Admin,
                Username = "admin-user",
                PasswordHash = "hash",
                RandomSalt = "salt",
            },
            new Account
            {
                Type = Account.AccountType.WebDav,
                Username = "webdav-user",
                PasswordHash = "hash",
                RandomSalt = "salt",
            });
        await _context.SaveChangesAsync();
    }
}
