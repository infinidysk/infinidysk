using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Api.SabControllers.Pause;
using NzbWebDAV.Api.SabControllers.Resume;
using NzbWebDAV.Api.SabControllers.SpeedLimit;
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
public sealed class SabPauseResumeTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Combine(Path.GetTempPath(), $"nzbdav-pause-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private QueueManager _queueManager = null!;
    private ConfigManager _configManager = null!;
    private WebsocketManager _websocketManager = null!;

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

        _websocketManager = new WebsocketManager();
        var usenet = new UsenetStreamingClient(
            _configManager,
            _websocketManager,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
        _queueManager = new QueueManager(
            usenet,
            _configManager,
            _websocketManager,
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
        try { Directory.Delete(_configRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Pause_PersistsPauseState_AndGetQueueReportsIt()
    {
        Assert.False(_configManager.IsSabQueuePaused());

        var response = await CreatePauseController().Pause(CancellationToken.None);

        Assert.True(response.Status);
        Assert.True(_configManager.IsSabQueuePaused());

        var persisted = await _context.ConfigItems.AsNoTracking()
            .SingleAsync(c => c.ConfigName == ConfigKeys.QueuePaused);
        Assert.Equal("true", persisted.ConfigValue);

        var queue = await CreateGetQueueController()
            .GetQueueAsync(new GetQueueRequest(new DefaultHttpContext(), _configManager));
        Assert.True(queue.Queue.Paused);
    }

    [Fact]
    public async Task Resume_ClearsPauseState_AndAwakensQueue()
    {
        await CreatePauseController().Pause(CancellationToken.None);
        Assert.True(_configManager.IsSabQueuePaused());

        var response = await CreateResumeController().Resume(CancellationToken.None);

        Assert.True(response.Status);
        Assert.False(_configManager.IsSabQueuePaused());

        var persisted = await _context.ConfigItems.AsNoTracking()
            .SingleAsync(c => c.ConfigName == ConfigKeys.QueuePaused);
        Assert.Equal("false", persisted.ConfigValue);

        var queue = await CreateGetQueueController()
            .GetQueueAsync(new GetQueueRequest(new DefaultHttpContext(), _configManager));
        Assert.False(queue.Queue.Paused);
    }

    [Fact]
    public async Task PauseThenResume_RoundTripsCleanly()
    {
        await CreatePauseController().Pause(CancellationToken.None);
        Assert.True(_configManager.IsSabQueuePaused());

        await CreateResumeController().Resume(CancellationToken.None);
        Assert.False(_configManager.IsSabQueuePaused());

        await CreatePauseController().Pause(CancellationToken.None);
        Assert.True(_configManager.IsSabQueuePaused());
    }

    [Fact]
    public async Task SpeedLimit_PersistsValue_AndReflectsInGetQueue()
    {
        Assert.Equal(0, _configManager.GetSabSpeedLimitKbps());

        var response = await CreateSpeedLimitController()
            .SetSpeedLimit(new SpeedLimitRequest { LimitKbps = 2048 }, CancellationToken.None);

        Assert.True(response.Status);
        Assert.Equal(2048, _configManager.GetSabSpeedLimitKbps());

        var persisted = await _context.ConfigItems.AsNoTracking()
            .SingleAsync(c => c.ConfigName == ConfigKeys.QueueSpeedLimitKbps);
        Assert.Equal("2048", persisted.ConfigValue);

        var queue = await CreateGetQueueController()
            .GetQueueAsync(new GetQueueRequest(new DefaultHttpContext(), _configManager));
        Assert.Equal("2048", queue.Queue.SpeedLimit);
        Assert.Equal("2048", queue.Queue.SpeedLimitAbs);
    }

    [Fact]
    public void SpeedLimitRequest_New_ParsesValueParam()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?value=512");

        var request = SpeedLimitRequest.New(context);

        Assert.Equal(512, request.LimitKbps);
    }

    [Fact]
    public void SpeedLimitRequest_New_ParsesLimitParamWhenValueMissing()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?limit=256");

        var request = SpeedLimitRequest.New(context);

        Assert.Equal(256, request.LimitKbps);
    }

    [Fact]
    public void SpeedLimitRequest_New_MissingValue_DefaultsToUnlimited()
    {
        var context = new DefaultHttpContext();

        var request = SpeedLimitRequest.New(context);

        Assert.Equal(0, request.LimitKbps);
    }

    [Fact]
    public void SpeedLimitRequest_New_NegativeValue_ThrowsBadRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?value=-5");

        Assert.Throws<BadHttpRequestException>(() => SpeedLimitRequest.New(context));
    }

    [Fact]
    public async Task GetTopQueueItem_SkipsPausedPriorityItems()
    {
        var pausedItem = CreateQueueItem("paused.nzb", QueueItem.PriorityOption.Paused, DateTime.UtcNow.AddMinutes(-10));
        var normalItem = CreateQueueItem("normal.nzb", QueueItem.PriorityOption.Normal, DateTime.UtcNow.AddMinutes(-5));
        _context.QueueItems.AddRange(pausedItem, normalItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var (queueItem, stream) = await _dbClient.GetTopQueueItem();
        stream?.Dispose();

        Assert.NotNull(queueItem);
        Assert.Equal(normalItem.Id, queueItem!.Id);
    }

    [Fact]
    public async Task GetTopQueueItem_AllItemsPaused_ReturnsNull()
    {
        var pausedItem = CreateQueueItem("paused.nzb", QueueItem.PriorityOption.Paused, DateTime.UtcNow);
        _context.QueueItems.Add(pausedItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var (queueItem, stream) = await _dbClient.GetTopQueueItem();
        stream?.Dispose();

        Assert.Null(queueItem);
    }

    [Theory]
    [InlineData("pause", typeof(PauseController))]
    [InlineData("resume", typeof(ResumeController))]
    [InlineData("speedlimit", typeof(SpeedLimitController))]
    public void GetController_DispatchesNewSabModes(string mode, Type expectedType)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?mode={mode}");

        var controller = CreateSabApiController(context).GetController();

        Assert.IsType(expectedType, controller);
    }

    [Theory]
    [InlineData("pause", typeof(PauseController))]
    [InlineData("resume", typeof(ResumeController))]
    public void GetController_DispatchesQueueNameAliases(string name, Type expectedType)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?mode=queue&name={name}");

        var controller = CreateSabApiController(context).GetController();

        Assert.IsType(expectedType, controller);
    }

    [Fact]
    public void GetController_UnknownMode_StillThrowsInvalidMode()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?mode=not-a-real-mode");

        var ex = Assert.Throws<BadHttpRequestException>(
            () => CreateSabApiController(context).GetController());
        Assert.Equal("Invalid mode", ex.Message);
    }

    private SabApiController CreateSabApiController(HttpContext httpContext)
    {
        var controller = new SabApiController(
            _dbClient,
            _configManager,
            _queueManager,
            _websocketManager,
            new ProviderUsageTracker(),
            new IndexerHitTracker());
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = httpContext,
        };
        return controller;
    }

    private PauseController CreatePauseController() =>
        new(new DefaultHttpContext(), _dbClient, _configManager);

    private ResumeController CreateResumeController() =>
        new(new DefaultHttpContext(), _dbClient, _configManager, _queueManager);

    private SpeedLimitController CreateSpeedLimitController() =>
        new(new DefaultHttpContext(), _dbClient, _configManager);

    private GetQueueController CreateGetQueueController() =>
        new(new DefaultHttpContext(), _dbClient, _queueManager, _configManager, new ProviderUsageTracker());

    private static QueueItem CreateQueueItem(
        string fileName, QueueItem.PriorityOption priority, DateTime createdAt)
    {
        return new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            NzbFileSize = 100,
            TotalSegmentBytes = 200,
            Category = "movies",
            Priority = priority,
            PostProcessing = QueueItem.PostProcessingOption.None
        };
    }
}
