using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(ConfigPathCollection))]
public sealed class SabLimitZeroTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-limit-zero-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private QueueManager _queueManager = null!;
    private ConfigManager _configManager = null!;

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

        _configManager = new ConfigManager();
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
        ]);

        var websocketManager = new WebsocketManager();
        var usenet = new UsenetStreamingClient(
            _configManager,
            websocketManager,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
        _queueManager = QueueManager.CreateForTests(
            usenet,
            _configManager,
            websocketManager,
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate());
    }

    public async Task DisposeAsync()
    {
        _queueManager.Dispose();
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task GetQueueAsync_LimitZero_ReturnsAllSeededItems()
    {
        var items = new[]
        {
            CreateQueueItem("first.nzb", DateTime.UtcNow.AddMinutes(-30)),
            CreateQueueItem("second.nzb", DateTime.UtcNow.AddMinutes(-20)),
            CreateQueueItem("third.nzb", DateTime.UtcNow.AddMinutes(-10)),
        };
        _context.QueueItems.AddRange(items);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?limit=0");
        var request = new GetQueueRequest(context, _configManager);

        var response = await CreateGetQueueController().GetQueueAsync(request);

        Assert.Equal(3, response.Queue.TotalCount);
        Assert.Equal(3, response.Queue.Slots.Count);
        Assert.Equal(
            items.Select(i => i.Id.ToString()).OrderBy(x => x),
            response.Queue.Slots.Select(s => s.NzoId).OrderBy(x => x));
    }

    [Fact]
    public async Task GetHistoryAsync_LimitZero_ReturnsAllSeededItems()
    {
        var items = new[]
        {
            CreateHistoryItem("first.nzb", DateTime.UtcNow.AddMinutes(-30)),
            CreateHistoryItem("second.nzb", DateTime.UtcNow.AddMinutes(-20)),
            CreateHistoryItem("third.nzb", DateTime.UtcNow.AddMinutes(-10)),
        };
        _context.HistoryItems.AddRange(items);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?limit=0");
        var request = new GetHistoryRequest(context, _configManager);

        var response = await CreateGetHistoryController().GetHistoryAsync(request);

        Assert.Equal(3, response.History.TotalCount);
        Assert.Equal(3, response.History.Slots.Count);
        Assert.Equal(
            items.Select(i => i.Id.ToString()).OrderBy(x => x),
            response.History.Slots.Select(s => s.NzoId).OrderBy(x => x));
    }

    private GetQueueController CreateGetQueueController() =>
        new(new DefaultHttpContext(), _dbClient, _queueManager, _configManager, new ProviderUsageTracker());

    private GetHistoryController CreateGetHistoryController() =>
        new(new DefaultHttpContext(), _dbClient, _configManager, new ProviderUsageTracker());

    private static QueueItem CreateQueueItem(string fileName, DateTime createdAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            NzbFileSize = 100,
            TotalSegmentBytes = 200,
            Category = "movies",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };

    private static HistoryItem CreateHistoryItem(string fileName, DateTime createdAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 100,
            DownloadTimeSeconds = 5,
        };
}
