using System.Diagnostics;
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
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Benchmarks;

internal static class SabApiReport
{
    private const string ReportName = "sab-api";
    private const int HistoryCount = 500;
    private const int QueueCount = 50;
    private const int TvHistoryCount = 50;
    private const string MoviesCategory = "movies";
    private const string TvCategory = "tv";

    public static async Task RunAsync(string jsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);

        var configRoot = Path.Join(Path.GetTempPath(), $"nzbdav-sab-api-report-{Guid.NewGuid():N}");
        var previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", configRoot);

        var scenarios = new Dictionary<string, ScenarioSnapshot>(StringComparer.Ordinal);
        try
        {
            var interceptor = new CountingDbCommandInterceptor();
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
                .AddInterceptors(new SqliteForeignKeyEnabler(), interceptor)
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            await using var dbContext = new DavDatabaseContext(options);
            await dbContext.Database.MigrateAsync().ConfigureAwait(false);
            var dbClient = new DavDatabaseClient(dbContext);

            var configManager = new ConfigManager();
            configManager.UpdateValues(
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
            using var metricsWriter = new MetricsWriter();
            using var usenet = new UsenetStreamingClient(
                configManager,
                websocketManager,
                new ProviderUsageTracker(),
                metricsWriter,
                new ProviderBytesTracker(),
                new StreamTraceBuffer(100),
                new ActiveReadRegistry());
            using var queueManager = new QueueManager(
                usenet,
                configManager,
                websocketManager,
                new ProviderUsageTracker(),
                new WatchdogLog(),
                new QueueItemSourceTracker(),
                new BenchmarkGate(),
                startLoop: false);

            SeedCorpus(dbContext);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            interceptor.Reset();

            await RecordQueueAsync(
                scenarios, dbClient, queueManager, configManager, interceptor, "queue-limit10", "?limit=10")
                .ConfigureAwait(false);
            await RecordQueueAsync(
                scenarios, dbClient, queueManager, configManager, interceptor, "queue-limit50", "?limit=50")
                .ConfigureAwait(false);
            await RecordQueueAsync(
                scenarios, dbClient, queueManager, configManager, interceptor, "queue-limit0", "?limit=0")
                .ConfigureAwait(false);
            await RecordHistoryAsync(
                scenarios, dbClient, configManager, interceptor, "history-limit10-start0", "?limit=10&start=0")
                .ConfigureAwait(false);
            await RecordHistoryAsync(
                scenarios, dbClient, configManager, interceptor,
                "history-limit10-start490", "?limit=10&start=490")
                .ConfigureAwait(false);
            await RecordHistoryAsync(
                scenarios, dbClient, configManager, interceptor, "history-limit0", "?limit=0")
                .ConfigureAwait(false);
            await RecordHistoryAsync(
                scenarios, dbClient, configManager, interceptor,
                "history-category-filter", $"?limit=10&cat={TvCategory}")
                .ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", previousConfigPath);
            try
            {
                Directory.Delete(configRoot, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }

        foreach (var (name, snapshot) in scenarios)
        {
            Console.WriteLine(
                $"{name} rows={snapshot.Deterministic["rowsReturned"]} " +
                $"total={snapshot.Deterministic["totalCount"]} " +
                $"db_commands={snapshot.Deterministic["dbCommands"]} " +
                $"elapsed_ms={snapshot.Timing["elapsedMs"]:F3} " +
                $"cpu_seconds={snapshot.Timing["cpuSeconds"]:F3}");
        }

        PerformanceReportJson.Write(jsonPath, ReportName, scenarios);
    }

    private static async Task RecordQueueAsync(
        Dictionary<string, ScenarioSnapshot> scenarios,
        DavDatabaseClient dbClient,
        QueueManager queueManager,
        ConfigManager configManager,
        CountingDbCommandInterceptor interceptor,
        string name,
        string query)
    {
        interceptor.Reset();
        var request = CreateQueueRequest(query, configManager);
        var controller = new GetQueueController(
            new DefaultHttpContext(), dbClient, queueManager, configManager, new ProviderUsageTracker());
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        var response = await controller.GetQueueAsync(request).ConfigureAwait(false);
        stopwatch.Stop();
        process.Refresh();
        var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
        scenarios[name] = new ScenarioSnapshot(
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["rowsReturned"] = response.Queue.Slots.Count,
                ["totalCount"] = response.Queue.TotalCount,
                ["dbCommands"] = interceptor.Count,
            },
            PerformanceReportJson.ApiTiming(stopwatch.Elapsed.TotalMilliseconds, cpuSeconds));
    }

    private static async Task RecordHistoryAsync(
        Dictionary<string, ScenarioSnapshot> scenarios,
        DavDatabaseClient dbClient,
        ConfigManager configManager,
        CountingDbCommandInterceptor interceptor,
        string name,
        string query)
    {
        interceptor.Reset();
        var request = CreateHistoryRequest(query, configManager);
        var controller = new GetHistoryController(
            new DefaultHttpContext(), dbClient, configManager, new ProviderUsageTracker());
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        var response = await controller.GetHistoryAsync(request).ConfigureAwait(false);
        stopwatch.Stop();
        process.Refresh();
        var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
        scenarios[name] = new ScenarioSnapshot(
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["rowsReturned"] = response.History.Slots.Count,
                ["totalCount"] = response.History.TotalCount,
                ["dbCommands"] = interceptor.Count,
            },
            PerformanceReportJson.ApiTiming(stopwatch.Elapsed.TotalMilliseconds, cpuSeconds));
    }

    private static GetQueueRequest CreateQueueRequest(string query, ConfigManager configManager)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return new GetQueueRequest(context, configManager);
    }

    private static GetHistoryRequest CreateHistoryRequest(string query, ConfigManager configManager)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return new GetHistoryRequest(context, configManager);
    }

    private static void SeedCorpus(DavDatabaseContext dbContext)
    {
        var epoch = DateTime.UnixEpoch;
        var queueItems = Enumerable.Range(0, QueueCount)
            .Select(index => new QueueItem
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
            });
        dbContext.QueueItems.AddRange(queueItems);

        var historyItems = Enumerable.Range(0, HistoryCount)
            .Select(index => new HistoryItem
            {
                Id = Guid.NewGuid(),
                CreatedAt = epoch.AddMinutes(index),
                FileName = $"job-{index:D4}.nzb",
                JobName = $"job-{index:D4}",
                Category = index < TvHistoryCount ? TvCategory : MoviesCategory,
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 1000 + index,
                DownloadTimeSeconds = 5,
            });
        dbContext.HistoryItems.AddRange(historyItems);
    }
}
