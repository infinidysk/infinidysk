using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Websocket;
using Xunit.Sdk;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class HealthCheckCoordinatorTests
{
    [Fact]
    public async Task DefaultWorkerCount_StartsOnlyOneFile()
    {
        using var harness = new Harness(workers: null, fullySplit: false);
        var ids = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid()]);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (_, ct) => blocker.Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Single(harness.Service.InProgressHealthCheckIds);
        Assert.Single(ids);
        blocker.TrySetResult();
        await WaitUntilAsync(() => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task CompletedWorker_IsReplacedWithoutWaitingForFixedBatch()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var ids = new Queue<Guid>([first, second, third]);
        var blockers = new ConcurrentDictionary<Guid, TaskCompletionSource>();
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (id, ct) => blockers
            .GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        Assert.Equal(2, harness.Service.InProgressHealthCheckIds.Count);

        blockers[first].TrySetResult();
        await WaitUntilAsync(() => !harness.Service.InProgressHealthCheckIds.Contains(first));
        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Contains(second, harness.Service.InProgressHealthCheckIds);
        Assert.Contains(third, harness.Service.InProgressHealthCheckIds);
        blockers[second].TrySetResult();
        blockers[third].TrySetResult();
        await WaitUntilAsync(() => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task FailedWorker_DoesNotStopOtherWorkers()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var failed = Guid.NewGuid();
        var running = Guid.NewGuid();
        var ids = new Queue<Guid>([failed, running]);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (id, ct) => id == failed
            ? Task.FromException(new InvalidOperationException("worker failure"))
            : blocker.Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        await WaitUntilAsync(() => !harness.Service.InProgressHealthCheckIds.Contains(failed));

        Assert.Contains(running, harness.Service.InProgressHealthCheckIds);
        blocker.TrySetResult();
        await WaitUntilAsync(() => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task Cancellation_DrainsAllActiveWorkers()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var ids = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid()]);
        using var cancellation = new CancellationTokenSource();
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (_, ct) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct);

        await harness.Service.RefillWorkerSlotsAsync(cancellation.Token);
        Assert.Equal(2, harness.Service.InProgressHealthCheckIds.Count);

        await cancellation.CancelAsync();
        await WaitUntilAsync(() => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task DuplicateReservation_IsRejectedByInMemoryGuard()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var id = Guid.NewGuid();
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.SelectCandidateOverride = (_, _, _) => Task.FromResult<Guid?>(id);
        harness.Service.ProcessCandidateOverride = (_, ct) => blocker.Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Equal(id, Assert.Single(harness.Service.InProgressHealthCheckIds));
        blocker.TrySetResult();
        await WaitUntilAsync(() => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task RaisingWorkerCount_FillsAdditionalSlots()
    {
        using var harness = new Harness(workers: 1, fullySplit: false);
        var ids = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid()]);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (_, ct) => blocker.Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        Assert.Single(harness.Service.InProgressHealthCheckIds);

        harness.SetWorkers(2);
        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Equal(2, harness.Service.InProgressHealthCheckIds.Count);
        blocker.TrySetResult();
        await WaitUntilAsync(() => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task LoweringWorkerCount_DoesNotCancelRunningWorkersOrStartReplacement()
    {
        using var harness = new Harness(workers: 2, fullySplit: false);
        var ids = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);
        var blockers = new ConcurrentDictionary<Guid, TaskCompletionSource>();
        harness.Service.SelectCandidateOverride = (_, _, _) =>
            Task.FromResult<Guid?>(ids.Count > 0 ? ids.Dequeue() : null);
        harness.Service.ProcessCandidateOverride = (id, ct) => blockers
            .GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .Task.WaitAsync(ct);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
        var running = harness.Service.InProgressHealthCheckIds.ToArray();
        harness.SetWorkers(1);
        blockers[running[0]].TrySetResult();
        await WaitUntilAsync(() => harness.Service.InProgressHealthCheckIds.Count == 1);

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.Single(harness.Service.InProgressHealthCheckIds);
        Assert.Single(ids);
        blockers[running[1]].TrySetResult();
        await WaitUntilAsync(() => harness.Service.InProgressHealthCheckIds.Count == 0);
    }

    [Fact]
    public async Task ActiveQueue_DefersNewWorkersWithSplitProviderBudgets()
    {
        using var harness = new Harness(workers: 2, fullySplit: true);
        var selectorCalled = false;
        harness.Service.HasActiveQueueItemsOverride = () => true;
        harness.Service.SelectCandidateOverride = (_, _, _) =>
        {
            selectorCalled = true;
            return Task.FromResult<Guid?>(Guid.NewGuid());
        };

        await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);

        Assert.False(selectorCalled);
        Assert.Empty(harness.Service.InProgressHealthCheckIds);
    }

    [Fact]
    public async Task ProductionSelection_PreservesOrderingAndExcludesUrgentDuringQueueActivity()
    {
        using var harness = new Harness(workers: 3, fullySplit: true);
        var databasePath = Path.Join(
            Path.GetTempPath(),
            $"infinidysk-health-selection-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var urgent = NewCandidate("urgent.mkv", DateTimeOffset.UnixEpoch);
        var neverChecked = NewCandidate("never-checked.mkv", null);
        var scheduled = NewCandidate("scheduled.mkv", DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
        var nonMedia = NewCandidate("notes.nfo", null);
        try
        {
            await using (var db = new DavDatabaseContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Items.AddRange(urgent, neverChecked, scheduled, nonMedia);
                await db.SaveChangesAsync();
            }
            harness.Service.CreateDbContextOverride = () => new DavDatabaseContext(options);

            var idleSelection = await harness.Service.SelectNextHealthCheckIdsAsync(
                [], allowUrgentRepair: true, maximumCount: 3, CancellationToken.None);
            var queueActiveSelection = await harness.Service.SelectNextHealthCheckIdsAsync(
                [neverChecked.Id], allowUrgentRepair: false, maximumCount: 3, CancellationToken.None);

            Assert.Equal([urgent.Id, neverChecked.Id, scheduled.Id], idleSelection);
            Assert.Equal([scheduled.Id], queueActiveSelection);
        }
        finally
        {
            try { File.Delete(databasePath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ProductionWorker_UsesItsOwnContextAndRecordsMissingPayload()
    {
        using var harness = new Harness(workers: 1, fullySplit: false);
        var databasePath = Path.Join(
            Path.GetTempPath(),
            $"infinidysk-health-worker-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var candidate = NewCandidate("missing-payload.mkv", null);
        try
        {
            await using (var db = new DavDatabaseContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Items.Add(candidate);
                await db.SaveChangesAsync();
            }
            harness.Service.CreateDbContextOverride = () => new DavDatabaseContext(options);

            await harness.Service.RefillWorkerSlotsAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.Service.InProgressHealthCheckIds.Count == 0);

            await using var verificationDb = new DavDatabaseContext(options);
            var result = Assert.Single(await verificationDb.HealthCheckResults.ToListAsync());
            Assert.Equal(candidate.Id, result.DavItemId);
            Assert.Contains("streaming data is missing", result.Message);
        }
        finally
        {
            try { File.Delete(databasePath); } catch (IOException) { }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new XunitException("Timed out waiting for the health coordinator condition.");
        }
    }

    private static DavItem NewCandidate(string name, DateTimeOffset? nextHealthCheck)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            Name = name,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = $"/library/{name}",
            NextHealthCheck = nextHealthCheck,
        };
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Join(
            Path.GetTempPath(),
            $"infinidysk-health-coordinator-{Guid.NewGuid():N}");
        private readonly UsenetStreamingClient _usenet;
        private readonly QueueManager _queueManager;
        private readonly HealthCheckConnectionGate _gate;
        private readonly RepairPatchStore _patchStore;

        public ConfigManager Config { get; }
        public HealthCheckService Service { get; }

        public Harness(int? workers, bool fullySplit)
        {
            Directory.CreateDirectory(_root);
            Config = new ConfigManager();
            var providerConfig = new UsenetProviderConfig();
            if (fullySplit)
            {
                providerConfig.Providers.Add(new UsenetProviderConfig.ConnectionDetails
                {
                    ProviderId = Guid.NewGuid(),
                    Type = ProviderType.Pooled,
                    Host = "split.example",
                    Port = 563,
                    UseSsl = true,
                    User = "user",
                    Pass = "pass",
                    MaxConnections = 10,
                });
            }

            var values = new List<ConfigItem>
            {
                new() { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
                new() { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _root },
                new()
                {
                    ConfigName = ConfigKeys.ArrInstances,
                    ConfigValue = JsonSerializer.Serialize(new ArrConfig
                    {
                        RadarrInstances =
                        [
                            new ArrConfig.ConnectionDetails
                            {
                                Host = "http://radarr.example",
                                ApiKey = "test",
                            },
                        ],
                    }),
                },
                new()
                {
                    ConfigName = ConfigKeys.UsenetProviders,
                    ConfigValue = JsonSerializer.Serialize(providerConfig),
                },
            };
            if (workers is { } workerCount)
            {
                values.Add(new ConfigItem
                {
                    ConfigName = ConfigKeys.RepairHealthcheckWorkers,
                    ConfigValue = workerCount.ToString(),
                });
            }
            Config.UpdateValues(values);

            var websocketManager = new WebsocketManager();
            _patchStore = new RepairPatchStore(Path.Join(_root, "patches"), 1024 * 1024);
            _usenet = new UsenetStreamingClient(
                Config,
                websocketManager,
                new ProviderUsageTracker(),
                new MetricsWriter(),
                new ProviderBytesTracker(),
                new StreamTraceBuffer(10),
                new ActiveReadRegistry(),
                repairPatchStore: _patchStore);
            _gate = new HealthCheckConnectionGate(Config);
            var benchmarkGate = new BenchmarkGate();
            _queueManager = QueueManager.CreateForTests(
                _usenet,
                Config,
                websocketManager,
                new ProviderUsageTracker(),
                new WatchdogLog(),
                new QueueItemSourceTracker(),
                benchmarkGate,
                startLoop: false,
                healthCheckConnectionGate: _gate);
            Service = new HealthCheckService(
                Config,
                _usenet,
                websocketManager,
                benchmarkGate,
                new StreamingFailureTracker(),
                _queueManager,
                new Par2RepairService(Config, _usenet, _patchStore),
                _patchStore,
                new ArrReplacementSearchBudget(),
                _gate);
        }

        public void SetWorkers(int count)
        {
            Config.UpdateValues([
                new ConfigItem
                {
                    ConfigName = ConfigKeys.RepairHealthcheckWorkers,
                    ConfigValue = count.ToString(),
                },
            ]);
        }

        public void Dispose()
        {
            Service.Dispose();
            _queueManager.Dispose();
            _gate.Dispose();
            _usenet.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
