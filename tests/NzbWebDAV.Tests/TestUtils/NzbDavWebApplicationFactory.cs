using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.TestUtils;

public sealed class NzbDavWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "integration-api-key";
    public const string WebDavUser = "integration-user";
    public const string WebDavPassword = "integration-password";

    private readonly string _configPath =
        Path.Join(Path.GetTempPath(), $"nzbdav-http-tests-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _previousEnvironment = new();
    private readonly FakeNntpClient? _fakeNntpClient;
    private int _disposed;

    public NzbDavWebApplicationFactory() : this(fakeNntpClient: null)
    {
    }

    internal NzbDavWebApplicationFactory(FakeNntpClient? fakeNntpClient)
    {
        _fakeNntpClient = fakeNntpClient;
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

    public string ConfigPath => _configPath;
    internal FakeNntpClient? FakeNntpClient => _fakeNntpClient;

    internal static NzbDavWebApplicationFactory CreateWithFakeNntp(
        IReadOnlyDictionary<string, byte[]> segments)
    {
        return new NzbDavWebApplicationFactory(
            new FakeNntpClient(segments, useCachedYencStreams: true));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (_fakeNntpClient is null)
            return;

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<UsenetStreamingClient>();
            services.AddSingleton(_ => new UsenetStreamingClient(_fakeNntpClient));
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", ApiKey);
        return client;
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

    public async Task AddDavNzbFileAsync(DavItem item, DavNzbFile file)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        context.Items.Add(item);
        context.NzbFiles.Add(file);
        await context.SaveChangesAsync();
    }

    public async Task WriteNzbBlobAsync(Guid id, byte[] nzbBytes)
    {
        _ = Services;
        await using var stream = new MemoryStream(nzbBytes);
        await BlobStore.WriteBlob(id, stream);
    }

    public async Task<QueueItem> SeedQueueItemAsync(
        Guid id,
        string fileName = "sample.nzb",
        string category = "tv",
        byte[]? nzbBytes = null)
    {
        await WriteNzbBlobAsync(id, nzbBytes ?? TestNzbs.SingleFile);
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var item = new QueueItem
        {
            Id = id,
            CreatedAt = DateTime.UtcNow,
            SortOrder = DateTime.UtcNow.Ticks,
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            NzbFileSize = nzbBytes?.Length ?? TestNzbs.SingleFile.Length,
            TotalSegmentBytes = 128,
            Category = category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };
        context.QueueItems.Add(item);
        context.NzbNames.Add(new NzbName { Id = id, FileName = fileName });
        await context.SaveChangesAsync();
        return item;
    }

    public async Task<HistoryItem> SeedHistoryItemAsync(
        Guid id,
        HistoryItem.DownloadStatusOption status = HistoryItem.DownloadStatusOption.Failed,
        string fileName = "sample.nzb",
        string category = "tv",
        byte[]? nzbBytes = null)
    {
        await WriteNzbBlobAsync(id, nzbBytes ?? TestNzbs.SingleFile);
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var item = new HistoryItem
        {
            Id = id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            Category = category,
            DownloadStatus = status,
            TotalSegmentBytes = 128,
            DownloadTimeSeconds = 5,
            FailMessage = status == HistoryItem.DownloadStatusOption.Failed
                ? "Timeout reading from NNTP stream."
                : "",
            NzbBlobId = id,
        };
        context.HistoryItems.Add(item);
        context.NzbNames.Add(new NzbName { Id = id, FileName = fileName });
        await context.SaveChangesAsync();
        return item;
    }

    public async Task WaitUntilQueueIdleAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        timeout ??= TimeSpan.FromSeconds(10);
        var queueManager = Services.GetRequiredService<QueueManager>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout.Value);
        while (queueManager.HasActiveQueueItems)
            await Task.Delay(25, cts.Token);
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
                _fakeNntpClient?.Dispose();
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
            .UseSqlite($"Data Source={Path.Join(_configPath, "db.sqlite")}")
            .AddInterceptors(new SqliteMainDbPragmas())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        using var databaseContext = new DavDatabaseContext(databaseOptions);
        databaseContext.Database.Migrate();

        var metricsOptions = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={Path.Join(_configPath, "metrics.sqlite")}")
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
