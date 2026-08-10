using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
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

namespace NzbWebDAV.Tests.Queue;

[Collection(nameof(ConfigPathCollection))]
public sealed class QueueManagerPauseResumeTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-qm-pr-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private QueueManager _queueManager = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);

        var configManager = new ConfigManager();
        var websocketManager = new WebsocketManager();
        var usenet = new UsenetStreamingClient(
            configManager,
            websocketManager,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
        _queueManager = new QueueManager(
            usenet,
            configManager,
            websocketManager,
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate(),
            startLoop: false);
    }

    public async Task DisposeAsync()
    {
        _queueManager.Dispose();
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task PauseQueueItemsAsync_SetsPausedPriority()
    {
        var item = CreateItem();
        _context.QueueItems.Add(item);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _queueManager.PauseQueueItemsAsync([item.Id], _dbClient);

        var updated = await _context.QueueItems.AsNoTracking().SingleAsync(q => q.Id == item.Id);
        Assert.Equal(QueueItem.PriorityOption.Paused, updated.Priority);
    }

    [Fact]
    public async Task ResumeQueueItemsAsync_ClearsPauseUntilAndSetsNormal()
    {
        var item = CreateItem();
        item.Priority = QueueItem.PriorityOption.Paused;
        item.PauseUntil = DateTime.UtcNow.AddHours(1);
        _context.QueueItems.Add(item);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _queueManager.ResumeQueueItemsAsync([item.Id], _dbClient);

        var updated = await _context.QueueItems.AsNoTracking().SingleAsync(q => q.Id == item.Id);
        Assert.Equal(QueueItem.PriorityOption.Normal, updated.Priority);
        Assert.Null(updated.PauseUntil);
    }

    private static QueueItem CreateItem() => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        FileName = "test.nzb",
        JobName = "test",
        NzbFileSize = 1,
        TotalSegmentBytes = 1,
        Category = "tv",
        Priority = QueueItem.PriorityOption.Normal,
        PostProcessing = QueueItem.PostProcessingOption.None,
    };
}
