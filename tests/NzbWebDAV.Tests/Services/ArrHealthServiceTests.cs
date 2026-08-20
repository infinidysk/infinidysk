using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class ArrHealthServiceTests
{
    [Fact]
    public void ComputeHandoffMs_ConvertsLocalCreatedAtToUtcAndClampsNegative()
    {
        var localCreated = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Unspecified);
        var createdUtc = DateTime.SpecifyKind(localCreated, DateTimeKind.Local).ToUniversalTime();
        var imported = new DateTimeOffset(createdUtc.AddSeconds(10), TimeSpan.Zero);

        Assert.Equal(10_000, ArrHealthMath.ComputeHandoffMs(imported, localCreated));
        Assert.Equal(0, ArrHealthMath.ComputeHandoffMs(imported.AddSeconds(-30), localCreated));
        Assert.Null(ArrHealthMath.ComputeHandoffMs(imported, null));
    }

    [Theory]
    [InlineData("completed", null, true)]
    [InlineData("Completed", null, true)]
    [InlineData("downloading", "importPending", true)]
    [InlineData("downloading", "importing", true)]
    [InlineData("downloading", "downloading", false)]
    [InlineData("paused", null, false)]
    public void IsAwaitingImport_MatchesCompletedAndImportStates(string? status, string? tracked, bool expected)
    {
        var record = new ArrQueueRecord { Status = status, TrackedDownloadState = tracked };
        Assert.Equal(expected, record.IsAwaitingImport);
    }

    [Fact]
    public async Task Ingest_ComputesHandoff_SkipsMissingHistory_AndClampsNegative()
    {
        await using var harness = await DualDbHarness.CreateAsync();
        var matchedId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var skewId = Guid.NewGuid();
        var localCreated = DateTime.Now.AddMinutes(-2);
        harness.Dav.HistoryItems.Add(CompletedHistory(matchedId, localCreated));
        harness.Dav.HistoryItems.Add(CompletedHistory(skewId, DateTime.Now.AddMinutes(5)));
        await harness.Dav.SaveChangesAsync();

        var imported = DateTimeOffset.UtcNow;
        var client = new ScriptedArrClient("http://sonarr:8989")
        {
            ImportHistory = (_, _, _) => Task.FromResult(new ArrHistory
            {
                Records =
                [
                    History(1, matchedId, imported, "matched"),
                    History(2, missingId, imported, "missing"),
                    History(3, skewId, imported, "skew"),
                ],
            }),
        };

        using var service = harness.CreateService(client, "http://sonarr:8989");
        Assert.True(await service.TryRunCycleAsync(CancellationToken.None));

        var events = await harness.Metrics.ArrImportEvents.AsNoTracking().OrderBy(e => e.ArrRecordId).ToListAsync();
        Assert.Equal(3, events.Count);
        Assert.NotNull(events[0].HandoffMs);
        Assert.True(events[0].HandoffMs >= 0);
        Assert.Null(events[1].HandoffMs);
        Assert.Equal(0, events[2].HandoffMs);
    }

    [Fact]
    public async Task Ingest_IsIncremental_Dedupes_SkipsNonGuid_AndCapsAtFivePages()
    {
        await using var harness = await DualDbHarness.CreateAsync();
        var guid = Guid.NewGuid();
        var pagesRequested = 0;
        var client = new ScriptedArrClient("http://sonarr:8989")
        {
            ImportHistory = (page, pageSize, _) =>
            {
                Interlocked.Increment(ref pagesRequested);
                Assert.Equal(100, pageSize);
                if (page > 6) return Task.FromResult(new ArrHistory());
                var startId = 600 - (page - 1) * 100;
                var records = Enumerable.Range(0, 100).Select(i =>
                {
                    var id = startId - i;
                    var downloadId = id == 599
                        ? "SABnzbd_nzo_abc"
                        : guid.ToString();
                    return History(id, downloadId, DateTimeOffset.UtcNow, $"t-{id}");
                }).ToList();
                return Task.FromResult(new ArrHistory { Records = records });
            },
        };

        using var service = harness.CreateService(client, "http://sonarr:8989");
        Assert.True(await service.TryRunCycleAsync(CancellationToken.None));
        Assert.Equal(5, pagesRequested);
        var firstCount = await harness.Metrics.ArrImportEvents.AsNoTracking().CountAsync();
        Assert.Equal(499, firstCount);

        pagesRequested = 0;
        Assert.True(await service.TryRunCycleAsync(CancellationToken.None));
        Assert.Equal(1, pagesRequested);
        Assert.Equal(firstCount, await harness.Metrics.ArrImportEvents.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task FailureIsolation_MarksOfflineAfterTwoFailures_WithoutBlockingOthers()
    {
        await using var harness = await DualDbHarness.CreateAsync();
        var healthy = new ScriptedArrClient("http://sonarr:8989");
        var broken = new ScriptedArrClient("http://radarr:7878")
        {
            QueueStatus = _ => throw new HttpRequestException("connection refused"),
        };
        var clients = new Dictionary<string, ArrClient>(StringComparer.Ordinal)
        {
            [healthy.Host] = healthy,
            [broken.Host] = broken,
        };

        var config = new ConfigManager();
        SetInstances(config,
            ("sonarr", healthy.Host, true),
            ("radarr", broken.Host, true));
        using var service = harness.CreateService(config, clients);

        Assert.True(await service.TryRunCycleAsync(CancellationToken.None));
        var first = service.GetSnapshots().ToDictionary(s => s.Host);
        Assert.Equal(ArrInstanceHealthStatus.Healthy, first[healthy.Host].Status);
        Assert.Equal(ArrInstanceHealthStatus.Pending, first[broken.Host].Status);

        Assert.True(await service.TryRunCycleAsync(CancellationToken.None));
        var second = service.GetSnapshots().ToDictionary(s => s.Host);
        Assert.Equal(ArrInstanceHealthStatus.Healthy, second[healthy.Host].Status);
        Assert.Equal(ArrInstanceHealthStatus.Offline, second[broken.Host].Status);
    }

    [Fact]
    public async Task Status_DegradedOnWarningsOrUnusualWait()
    {
        await using var harness = await DualDbHarness.CreateAsync();
        var warningsClient = new ScriptedArrClient("http://sonarr:8989")
        {
            QueueStatus = _ => Task.FromResult(new ArrQueueStatus { Warnings = true, TotalCount = 2 }),
        };
        using (var warningsService = harness.CreateService(warningsClient, "http://sonarr:8989"))
        {
            Assert.True(await warningsService.TryRunCycleAsync(CancellationToken.None));
            Assert.Equal(ArrInstanceHealthStatus.Degraded, Assert.Single(warningsService.GetSnapshots()).Status);
        }

        var downloadId = Guid.NewGuid();
        var createdAt = DateTime.Now.AddMinutes(-10);
        harness.Dav.HistoryItems.Add(CompletedHistory(downloadId, createdAt));
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 5; i++)
        {
            harness.Metrics.ArrImportEvents.Add(new ArrImportEvent
            {
                InstanceKey = "sonarr|http://sonarr-median:8989",
                ArrRecordId = i + 1,
                DownloadId = Guid.NewGuid(),
                ImportedAtMs = nowMs - i * 1000,
                HandoffMs = 10_000,
                Title = $"sample-{i}",
            });
        }

        await harness.Dav.SaveChangesAsync();
        await harness.Metrics.SaveChangesAsync();

        var unusualClient = new ScriptedArrClient("http://sonarr-median:8989")
        {
            Queue = _ => Task.FromResult(new ArrQueue<ArrQueueRecord>
            {
                Records =
                [
                    new ArrQueueRecord
                    {
                        Title = "stuck",
                        Status = "completed",
                        DownloadId = downloadId.ToString(),
                    },
                ],
            }),
        };
        var config = new ConfigManager();
        SetInstances(config, ("sonarr", unusualClient.Host, true));
        using var unusualService = harness.CreateService(config, new Dictionary<string, ArrClient>
        {
            [unusualClient.Host] = unusualClient,
        });
        Assert.True(await unusualService.TryRunCycleAsync(CancellationToken.None));
        var snap = Assert.Single(unusualService.GetSnapshots());
        Assert.Equal(ArrInstanceHealthStatus.Degraded, snap.Status);
        Assert.Equal(5, snap.MedianSampleCount30d);
        Assert.Equal(10_000, snap.MedianHandoffMs30d);
        Assert.True(ArrHealthMath.IsUnusual(
            ArrHealthMath.ComputeWaitingMs(createdAt, DateTimeOffset.UtcNow),
            snap.MedianHandoffMs30d,
            snap.MedianSampleCount30d));
    }

    [Fact]
    public async Task Dormancy_DoesNotPollWhenDisabledOrWhenNoEnabledInstances()
    {
        await using var harness = await DualDbHarness.CreateAsync();
        var calls = 0;
        var client = new ScriptedArrClient("http://sonarr:8989")
        {
            QueueStatus = _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new ArrQueueStatus());
            },
        };

        var config = new ConfigManager();
        using var service = harness.CreateService(config, new Dictionary<string, ArrClient>
        {
            [client.Host] = client,
        });
        service.MetricsContextFactory = () => throw new InvalidOperationException("metrics must stay dormant");
        service.DavContextFactory = () => throw new InvalidOperationException("dav must stay dormant");

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(250);
        Assert.Equal(0, calls);
        Assert.Equal(0, service.CycleAttempts);

        SetInstances(config, ("sonarr", client.Host, false));
        await Task.Delay(250);
        Assert.Equal(0, calls);

        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.ArrHealthEnabled, ConfigValue = "false" },
        ]);
        SetInstancesEnabled(config, ("sonarr", client.Host, true));
        await Task.Delay(250);
        Assert.Equal(0, calls);

        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CycleOverlap_SkipsWhenACycleIsAlreadyRunning()
    {
        await using var harness = await DualDbHarness.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedArrClient("http://sonarr:8989")
        {
            QueueStatus = async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return new ArrQueueStatus();
            },
        };
        using var service = harness.CreateService(client, "http://sonarr:8989");

        var first = service.TryRunCycleAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await service.TryRunCycleAsync(CancellationToken.None));
        release.TrySetResult();
        Assert.True(await first);
        Assert.Equal(1, service.CycleAttempts);
    }

    [Fact]
    public async Task UniqueConstraint_OnDuplicateArrRecordId_IsTolerated()
    {
        await using var harness = await DualDbHarness.CreateAsync();
        var downloadId = Guid.NewGuid();
        var client = new ScriptedArrClient("http://sonarr:8989")
        {
            ImportHistory = (page, _, _) => Task.FromResult(new ArrHistory
            {
                Records = page <= 2
                    ? [History(10, downloadId, DateTimeOffset.UtcNow, "dup")]
                    : [],
            }),
        };
        using var service = harness.CreateService(client, "http://sonarr:8989");
        Assert.True(await service.TryRunCycleAsync(CancellationToken.None));
        harness.Metrics.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Metrics.ArrImportEvents.CountAsync());
    }

    private static ArrHistoryRecord History(int id, Guid downloadId, DateTimeOffset date, string title) =>
        History(id, downloadId.ToString(), date, title);

    private static ArrHistoryRecord History(int id, string downloadId, DateTimeOffset date, string title) =>
        new()
        {
            Id = id,
            Date = date,
            DownloadId = downloadId,
            EventType = ArrHealthService.ImportEventType,
            SourceTitle = title,
        };

    private static HistoryItem CompletedHistory(Guid id, DateTime createdAt) =>
        new()
        {
            Id = id,
            CreatedAt = createdAt,
            FileName = $"{id}.nzb",
            JobName = "job",
            Category = "tv",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1,
            DownloadTimeSeconds = 1,
        };

    private static void SetInstances(ConfigManager config, params (string AppType, string Host, bool Enabled)[] instances)
    {
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.ArrHealthEnabled, ConfigValue = "true" },
        ]);
        SetInstancesEnabled(config, instances);
    }

    private static void SetInstancesEnabled(ConfigManager config, params (string AppType, string Host, bool Enabled)[] instances)
    {
        var arr = new ArrConfig();
        foreach (var instance in instances)
        {
            var details = new ArrConfig.ConnectionDetails
            {
                Host = instance.Host,
                ApiKey = "k",
                Enabled = instance.Enabled,
            };
            if (instance.AppType == "radarr") arr.RadarrInstances.Add(details);
            else arr.SonarrInstances.Add(details);
        }

        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.ArrInstances, ConfigValue = JsonSerializer.Serialize(arr) },
        ]);
    }

    private sealed class DualDbHarness : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly string _metricsPath;
        private readonly string _davPath;

        private DualDbHarness(string dir, string metricsPath, string davPath, MetricsDbContext metrics, DavDatabaseContext dav)
        {
            _dir = dir;
            _metricsPath = metricsPath;
            _davPath = davPath;
            Metrics = metrics;
            Dav = dav;
        }

        public MetricsDbContext Metrics { get; }
        public DavDatabaseContext Dav { get; }

        public static async Task<DualDbHarness> CreateAsync()
        {
            var dir = Path.Join(Path.GetTempPath(), $"nzbdav-arr-health-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var metricsPath = Path.Join(dir, "metrics.sqlite");
            var davPath = Path.Join(dir, "db.sqlite");
            var metricsOptions = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={metricsPath}")
                .AddInterceptors(new SqliteMetricsPragmas())
                .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            var davOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={davPath}")
                .AddInterceptors(new SqliteForeignKeyEnabler())
                .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            var metrics = new MetricsDbContext(metricsOptions);
            var dav = new DavDatabaseContext(davOptions);
            await metrics.Database.MigrateAsync();
            await dav.Database.MigrateAsync();
            return new DualDbHarness(dir, metricsPath, davPath, metrics, dav);
        }

        public ArrHealthService CreateService(ScriptedArrClient client, string host)
        {
            var config = new ConfigManager();
            SetInstances(config, ("sonarr", host, true));
            return CreateService(config, new Dictionary<string, ArrClient> { [host] = client });
        }

        public ArrHealthService CreateService(ConfigManager config, IReadOnlyDictionary<string, ArrClient> clients)
        {
            return new ArrHealthService(config)
            {
                ClientFactory = (_, details) => clients[details.Host],
                MetricsContextFactory = CloneMetrics,
                DavContextFactory = CloneDav,
            };
        }

        private MetricsDbContext CloneMetrics()
        {
            var options = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={_metricsPath}")
                .AddInterceptors(new SqliteMetricsPragmas())
                .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            return new MetricsDbContext(options);
        }

        private DavDatabaseContext CloneDav()
        {
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={_davPath}")
                .AddInterceptors(new SqliteForeignKeyEnabler())
                .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            return new DavDatabaseContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await Metrics.DisposeAsync();
            await Dav.DisposeAsync();
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    private sealed class ScriptedArrClient : ArrClient
    {
        public Func<CancellationToken, Task<ArrQueueStatus>> QueueStatus { get; init; } =
            _ => Task.FromResult(new ArrQueueStatus());

        public Func<CancellationToken, Task<ArrQueue<ArrQueueRecord>>> Queue { get; init; } =
            _ => Task.FromResult(new ArrQueue<ArrQueueRecord>());

        public Func<int, int, CancellationToken, Task<ArrHistory>> ImportHistory { get; init; } =
            (_, _, _) => Task.FromResult(new ArrHistory());

        public ScriptedArrClient(string host) : base(host, "test-key")
        {
        }

        public override Task<ArrQueueStatus> GetQueueStatusAsync(CancellationToken ct = default) => QueueStatus(ct);

        public override Task<ArrQueue<ArrQueueRecord>> GetQueueAsync(CancellationToken ct = default) => Queue(ct);

        public override Task<ArrHistory> GetImportHistoryAsync(int page, int pageSize, CancellationToken ct = default) =>
            ImportHistory(page, pageSize, ct);
    }
}
