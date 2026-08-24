using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Services;

public sealed class HealthCheckWorkerAdmissionTests
{
    [Fact]
    public async Task StartupGrace_CancellationStopsBeforeQueueAdmission()
    {
        var queue = new RecordingQueueCoordinator(active: false);
        await using var fixture = await HealthFixture.CreateAsync(queue);
        var previousGrace = HealthCheckService.StartupGracePeriod;
        HealthCheckService.StartupGracePeriod = TimeSpan.FromHours(1);
        try
        {
            await fixture.Service.StartAsync(CancellationToken.None);
            await fixture.Service.StopAsync(CancellationToken.None);

            Assert.Equal(0, queue.ActiveReadCount);
        }
        finally
        {
            HealthCheckService.StartupGracePeriod = previousGrace;
        }
    }

    [Fact]
    public async Task ActiveQueue_DefersHealthWorkUntilStopped()
    {
        var queue = new RecordingQueueCoordinator(active: true);
        await using var fixture = await HealthFixture.CreateAsync(queue);
        var previousGrace = HealthCheckService.StartupGracePeriod;
        HealthCheckService.StartupGracePeriod = TimeSpan.Zero;
        try
        {
            await fixture.Service.StartAsync(CancellationToken.None);
            await queue.FirstActiveRead.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, queue.ActiveReadCount);
            fixture.Time.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(1, queue.ActiveReadCount);
        }
        finally
        {
            await fixture.Service.StopAsync(CancellationToken.None);
            HealthCheckService.StartupGracePeriod = previousGrace;
        }
    }

    private sealed class HealthFixture : IAsyncDisposable
    {
        private readonly string _patchDirectory;

        private HealthFixture(HealthCheckService service, ControllableTimeProvider time, string patchDirectory)
        {
            Service = service;
            Time = time;
            _patchDirectory = patchDirectory;
        }

        public HealthCheckService Service { get; }
        public ControllableTimeProvider Time { get; }

        public static async Task<HealthFixture> CreateAsync(IQueueCoordinator queue)
        {
            var patchDirectory = Path.Join(Path.GetTempPath(), $"nzbdav-health-worker-{Guid.NewGuid():N}");
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            ]);
            var patchStore = new RepairPatchStore(patchDirectory, 1024 * 1024);
            await patchStore.CatalogLoadTask;
            var time = new ControllableTimeProvider();
            var service = new HealthCheckService(
                config,
                null!,
                new WebsocketManager(),
                new BenchmarkGate(),
                new StreamingFailureTracker(),
                queue,
                new Par2RepairService(config, null!, patchStore),
                patchStore,
                new ArrReplacementSearchBudget(),
                timeProvider: time);
            return new HealthFixture(service, time, patchDirectory);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_patchDirectory))
                Directory.Delete(_patchDirectory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingQueueCoordinator(bool active) : IQueueCoordinator
    {
        private readonly bool _active = active;
        public TaskCompletionSource FirstActiveRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActiveReadCount { get; private set; }

        public bool HasActiveQueueItems
        {
            get
            {
                ActiveReadCount++;
                FirstActiveRead.TrySetResult();
                return _active;
            }
        }

        public IReadOnlyList<QueueManager.InProgressQueueItemSnapshot> GetInProgressQueueItems() => [];
        public QueueManager.InProgressQueueItemSnapshot? FindInProgressQueueItem(Guid queueItemId) => null;
        public IDisposable? TryReserveQueueSlot(int persistedCount, int maxItems, int resumeThreshold) => null;
        public void AwakenQueue(DateTime? dateTime = null) { }
        public Task RemoveQueueItemsAsync(List<Guid> queueItemIds, DavDatabaseClient dbClient, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseQueueItemsAsync(List<Guid> queueItemIds, DavDatabaseClient dbClient, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeQueueItemsAsync(List<Guid> queueItemIds, DavDatabaseClient dbClient, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQueueItemsPriorityAsync(List<Guid> queueItemIds, QueueItem.PriorityOption priority, DavDatabaseClient dbClient, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DavDatabaseClient.QueueSwitchResult> SwitchQueueItemAsync(Guid sourceId, string target, DavDatabaseClient dbClient, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<Guid>> MoveQueueItemsToTopAsync(List<Guid> queueItemIds, DavDatabaseClient dbClient, CancellationToken ct = default) => Task.FromResult(queueItemIds);
        public Task<List<Guid>> SetQueueItemsCategoryAsync(List<Guid> queueItemIds, string category, DavDatabaseClient dbClient, CancellationToken ct = default) => Task.FromResult(queueItemIds);
    }
}
