using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
public sealed class SabApiResponseShapeTests : IAsyncLifetime
{
    private const int HistoryCount = 500;
    private const int QueueCount = 50;
    private const int TvHistoryCount = 50;
    private const string MoviesCategory = "movies";
    private const string TvCategory = "tv";
    private const string QueryCountMessage =
        "Intentional query-shape change ⇒ update this assertion and the committed SAB API baseline.";

    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-sab-shape-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private bool _configPathOverridden;
    private CountingDbCommandInterceptor _interceptor = null!;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private QueueManager _queueManager = null!;
    private ConfigManager _configManager = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        try
        {
            Directory.CreateDirectory(_configRoot);
            Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);
            _configPathOverridden = true;

            _interceptor = new CountingDbCommandInterceptor();
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
                .AddInterceptors(new SqliteForeignKeyEnabler(), _interceptor)
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            _context = new DavDatabaseContext(options);
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
                new ConfigItem
                {
                    ConfigName = ConfigKeys.ApiIgnoreHistoryLimit,
                    ConfigValue = "false",
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
            _queueManager = new QueueManager(
                usenet,
                _configManager,
                websocketManager,
                new ProviderUsageTracker(),
                new WatchdogLog(),
                new QueueItemSourceTracker(),
                new BenchmarkGate());

            SeedCorpus(HistoryCount, QueueCount);
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();
            _interceptor.Reset();
        }
        catch
        {
            try
            {
                await DisposeAsync();
            }
            catch
            {
                // best-effort teardown after a failed InitializeAsync
            }

            throw;
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            var queueManager = _queueManager;
            _queueManager = null!;
            queueManager?.Dispose();

            var context = _context;
            _context = null!;
            if (!ReferenceEquals(context, null))
                await context.DisposeAsync();
        }
        finally
        {
            if (_configPathOverridden)
            {
                Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
                _configPathOverridden = false;
            }

            try
            {
                if (Directory.Exists(_configRoot))
                    Directory.Delete(_configRoot, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    [Fact]
    public async Task QueueAndHistory_PaginationFingerprintsMatchSeededCorpus()
    {
        await AssertQueueAsync("?limit=10", expectedRows: 10, expectedTotal: QueueCount);
        await AssertQueueAsync("?limit=50", expectedRows: QueueCount, expectedTotal: QueueCount);
        await AssertQueueAsync("?limit=0", expectedRows: QueueCount, expectedTotal: QueueCount);

        await AssertHistoryAsync("?limit=10&start=0", expectedRows: 10, expectedTotal: HistoryCount);
        await AssertHistoryAsync("?limit=10&start=490", expectedRows: 10, expectedTotal: HistoryCount);
        await AssertHistoryAsync("?limit=0", expectedRows: HistoryCount, expectedTotal: HistoryCount);
        await AssertHistoryAsync($"?limit=10&cat={TvCategory}", expectedRows: 10, expectedTotal: TvHistoryCount);
    }

    [Fact]
    public async Task HistoryPagedQuery_CommandCountIsInvariantWhenCorpusDoubles()
    {
        const string pagedQuery = "?limit=10&start=0";
        _interceptor.Reset();
        var first = await CreateGetHistoryController().GetHistoryAsync(
            CreateHistoryRequest(pagedQuery));
        var commandsAt500 = _interceptor.Count;
        Assert.Equal(10, first.History.Slots.Count);
        Assert.Equal(HistoryCount, first.History.TotalCount);
        Assert.True(commandsAt500 > 0, QueryCountMessage);

        SeedHistory(HistoryCount, HistoryCount);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        _interceptor.Reset();
        var doubled = await CreateGetHistoryController().GetHistoryAsync(
            CreateHistoryRequest(pagedQuery));
        Assert.Equal(10, doubled.History.Slots.Count);
        Assert.Equal(HistoryCount * 2, doubled.History.TotalCount);
        Assert.True(
            commandsAt500 == _interceptor.Count,
            $"history-limit10 dbCommands expected {commandsAt500} after doubling corpus but was {_interceptor.Count}. {QueryCountMessage}");
    }

    private async Task AssertQueueAsync(string query, int expectedRows, int expectedTotal)
    {
        _interceptor.Reset();
        var response = await CreateGetQueueController().GetQueueAsync(CreateQueueRequest(query));
        Assert.Equal(expectedRows, response.Queue.Slots.Count);
        Assert.Equal(expectedTotal, response.Queue.TotalCount);
        Assert.True(
            _interceptor.Count == 2,
            $"queue {query} dbCommands expected 2 but was {_interceptor.Count}. {QueryCountMessage}");
    }

    private async Task AssertHistoryAsync(string query, int expectedRows, int expectedTotal)
    {
        _interceptor.Reset();
        var response = await CreateGetHistoryController().GetHistoryAsync(CreateHistoryRequest(query));
        Assert.Equal(expectedRows, response.History.Slots.Count);
        Assert.Equal(expectedTotal, response.History.TotalCount);
        Assert.True(
            _interceptor.Count == 2,
            $"history {query} dbCommands expected 2 but was {_interceptor.Count}. {QueryCountMessage}");
    }

    private GetQueueController CreateGetQueueController() =>
        new(new DefaultHttpContext(), _dbClient, _queueManager, _configManager, new ProviderUsageTracker());

    private GetHistoryController CreateGetHistoryController() =>
        new(new DefaultHttpContext(), _dbClient, _configManager, new ProviderUsageTracker());

    private GetQueueRequest CreateQueueRequest(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return new GetQueueRequest(context, _configManager);
    }

    private GetHistoryRequest CreateHistoryRequest(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return new GetHistoryRequest(context, _configManager);
    }

    private void SeedCorpus(int historyCount, int queueCount)
    {
        SeedQueue(queueCount);
        SeedHistory(0, historyCount);
    }

    private void SeedQueue(int count)
    {
        var epoch = DateTime.UnixEpoch;
        _context.QueueItems.AddRange(Enumerable.Range(0, count).Select(index => new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = epoch.AddMinutes(index),
            SortOrder = index,
            FileName = $"job-{index:D4}.nzb",
            JobName = $"job-{index:D4}",
            NzbFileSize = 1000 + index,
            TotalSegmentBytes = 2000 + index,
            Category = MoviesCategory,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        }));
    }

    private void SeedHistory(int startIndex, int count)
    {
        var epoch = DateTime.UnixEpoch;
        _context.HistoryItems.AddRange(Enumerable.Range(startIndex, count).Select(index => new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = epoch.AddMinutes(index),
            FileName = $"job-{index:D4}.nzb",
            JobName = $"job-{index:D4}",
            Category = index < TvHistoryCount ? TvCategory : MoviesCategory,
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1000 + index,
            DownloadTimeSeconds = 5,
        }));
    }

    private sealed class CountingDbCommandInterceptor : DbCommandInterceptor
    {
        private int _count;
        public int Count => _count;
        public void Reset() => _count = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            _count++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            _count++;
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            _count++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            _count++;
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _count++;
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
