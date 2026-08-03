using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.TestUtils;

public sealed class NzbDavWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "integration-api-key";
    public const string WebDavUser = "integration-user";
    public const string WebDavPassword = "integration-password";

    private readonly string _configPath =
        Path.Combine(Path.GetTempPath(), $"nzbdav-http-tests-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _previousEnvironment = new();
    private int _disposed;

    public NzbDavWebApplicationFactory()
    {
        Directory.CreateDirectory(_configPath);
        SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        SetEnvironmentVariable("CONFIG_PATH", _configPath);
        SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", ApiKey);
        SetEnvironmentVariable("WEBDAV_USER", WebDavUser);
        SetEnvironmentVariable("WEBDAV_PASSWORD", WebDavPassword);
        SetEnvironmentVariable("DISABLE_WEBDAV_AUTH", null);
        SetEnvironmentVariable("LOG_LEVEL", "Warning");
        ResetDefaultDatabaseOptions();
        InitializeDatabases();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }

    public HttpRequestMessage CreateWebDavRequest(HttpMethod method, string path, string? depth = null)
    {
        var request = new HttpRequestMessage(method, path);
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{WebDavUser}:{WebDavPassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        if (depth is not null)
            request.Headers.TryAddWithoutValidation("Depth", depth);
        return request;
    }

    public async Task AddDavItemsAsync(params DavItem[] items)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        context.Items.AddRange(items);
        await context.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                SqliteConnection.ClearAllPools();
                foreach (var variable in _previousEnvironment)
                    Environment.SetEnvironmentVariable(variable.Key, variable.Value);
                ResetDefaultDatabaseOptions();

                try
                {
                    Directory.Delete(_configPath, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup for transient SQLite file handles.
                }
            }
        }
    }

    private void SetEnvironmentVariable(string name, string? value)
    {
        _previousEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private void InitializeDatabases()
    {
        var databaseOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={Path.Combine(_configPath, "db.sqlite")}")
            .AddInterceptors(new SqliteMainDbPragmas())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        using var databaseContext = new DavDatabaseContext(databaseOptions);
        databaseContext.Database.Migrate();

        var metricsOptions = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_configPath, "metrics.sqlite")}")
            .AddInterceptors(new SqliteMetricsPragmas())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        using var metricsContext = new MetricsDbContext(metricsOptions);
        metricsContext.Database.Migrate();
    }

    private static void ResetDefaultDatabaseOptions()
    {
        DavDatabaseContext.ResetOptionsForTests();
        MetricsDbContext.ResetOptionsForTests();
    }
}
