using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Queue;

public class QueueManagerLoopTests
{
    [Fact]
    public async Task ProcessQueueAsync_BacksOffOnPersistentFetchErrors()
    {
        using var manager = CreateManager();
        var iterations = 0;
        manager.ErrorBackoffDelay = TimeSpan.FromSeconds(5);
        manager.GetTopQueueItemOverride = (_, _) =>
        {
            Interlocked.Increment(ref iterations);
            throw new InvalidOperationException("persistent db failure");
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await manager.ProcessQueueAsync(cts.Token);

        // Without backoff this would spin dozens of times; with 5s backoff, ≤2 in 8s.
        Assert.InRange(iterations, 1, 2);
    }

    [Fact]
    public async Task ProcessQueueAsync_WakesBeforeIdleDelayWhenPauseExpires()
    {
        using var manager = CreateManager();
        manager.IdleDelay = TimeSpan.FromSeconds(30);

        var pollTimes = new List<DateTime>();
        var pauseCalls = 0;
        manager.GetTopQueueItemOverride = (_, _) =>
        {
            lock (pollTimes) pollTimes.Add(DateTime.UtcNow);
            return Task.FromResult<(QueueItem? queueItem, Stream? queueNzbStream)>((null, null));
        };
        manager.GetNextPauseUntilOverride = _ =>
        {
            var call = Interlocked.Increment(ref pauseCalls);
            return Task.FromResult<DateTime?>(
                call == 1 ? DateTime.Now.AddSeconds(2) : null);
        };

        using var cts = new CancellationTokenSource();
        var loop = manager.ProcessQueueAsync(cts.Token);

        // Wait until we've seen a second top-item poll (pause-aware wake), then stop.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            lock (pollTimes)
            {
                if (pollTimes.Count >= 2) break;
            }
            await Task.Delay(50);
        }

        await cts.CancelAsync();
        await loop;

        DateTime first;
        DateTime second;
        lock (pollTimes)
        {
            Assert.True(pollTimes.Count >= 2, $"Expected ≥2 polls, got {pollTimes.Count}");
            first = pollTimes[0];
            second = pollTimes[1];
        }

        var gap = second - first;
        Assert.True(gap < TimeSpan.FromSeconds(5),
            $"Second poll should wake within ~5s of pause expiry, took {gap.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task ProcessQueueAsync_ExitsIdleSleepPromptlyOnShutdown()
    {
        using var manager = CreateManager();
        manager.IdleDelay = TimeSpan.FromMinutes(1);
        manager.GetTopQueueItemOverride = (_, _) =>
            Task.FromResult<(QueueItem? queueItem, Stream? queueNzbStream)>((null, null));

        using var cts = new CancellationTokenSource();
        var loop = manager.ProcessQueueAsync(cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        var completed = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(loop, completed);
        await loop; // observe completion / no fault
    }

    [Fact]
    public async Task RemoveQueueItemsAsync_CancellationInterruptsContendedStateLock()
    {
        using var manager = CreateManager();
        var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.GetTopQueueItemOverride = async (_, _) =>
        {
            lockEntered.TrySetResult();
            await releaseLock.Task;
            return (null, null);
        };

        using var loopCancellation = new CancellationTokenSource();
        var loop = manager.ProcessQueueAsync(loopCancellation.Token);
        await lockEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        using var requestCancellation = new CancellationTokenSource();
        var remove = manager.RemoveQueueItemsAsync([], null!, requestCancellation.Token);
        await requestCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await remove);

        releaseLock.TrySetResult();
        await loopCancellation.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task WaitForWorkerOrAwaken_ConsumesAwakenSignalInsteadOfSpinning()
    {
        using var manager = CreateManager();

        // Stand in for an in-progress worker that never finishes.
        var runningWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // An addfile (e.g. a new grab) awakens the queue while a worker runs.
        manager.AwakenQueue();

        using var cts = new CancellationTokenSource();
        var firstWait = manager.WaitForWorkerOrAwakenAsync([runningWorker.Task], cts.Token);
        Assert.True(await firstWait.WaitAsync(TimeSpan.FromSeconds(1)));

        // With the signal consumed, the next wait must block on the poll/worker
        // instead of returning instantly. Before the fix the token stayed latched
        // and this returned immediately every iteration, spinning a core.
        var secondWait = manager.WaitForWorkerOrAwakenAsync([runningWorker.Task], cts.Token);
        var winner = await Task.WhenAny(secondWait, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(secondWait, winner);

        runningWorker.SetResult();
        await cts.CancelAsync();
        await secondWait;
    }

    [Fact]
    public async Task WaitForWorkerOrAwaken_ReturnsFalseOnShutdown()
    {
        using var manager = CreateManager();
        var runningWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = new CancellationTokenSource();
        var wait = manager.WaitForWorkerOrAwakenAsync([runningWorker.Task], cts.Token);

        // Shutdown while a worker is still running must break the coordinator loop.
        await cts.CancelAsync();
        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(1)));

        runningWorker.SetResult();
    }

    [Fact]
    public async Task WaitForWorkerOrAwaken_TimedAwakenDoesNotSpin()
    {
        using var manager = CreateManager();
        var runningWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A future pause schedules a timed wake. Until it fires the token is not
        // cancelled, so the wait must block on the poll rather than resetting and
        // returning instantly the way a fired awaken does.
        manager.AwakenQueue(DateTime.Now.AddSeconds(5));

        using var cts = new CancellationTokenSource();
        var wait = manager.WaitForWorkerOrAwakenAsync([runningWorker.Task], cts.Token);
        var winner = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(wait, winner);

        runningWorker.SetResult();
        await cts.CancelAsync();
        await wait;
    }

    [Fact]
    public async Task ProcessQueueAsync_SkipsDequeueWhileSabQueuePaused_AndResumesOnAwaken()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
            new ConfigItem { ConfigName = ConfigKeys.QueuePaused, ConfigValue = "true" },
        ]);

        using var manager = CreateManager(config);
        var polls = 0;
        manager.GetTopQueueItemOverride = (_, _) =>
        {
            Interlocked.Increment(ref polls);
            return Task.FromResult<(QueueItem? queueItem, Stream? queueNzbStream)>((null, null));
        };

        using var cts = new CancellationTokenSource();
        var loop = manager.ProcessQueueAsync(cts.Token);

        // While paused, the coordinator must not claim work.
        await Task.Delay(350);
        Assert.Equal(0, Volatile.Read(ref polls));

        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.QueuePaused, ConfigValue = "false" },
        ]);
        manager.AwakenQueue();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref polls) == 0)
            await Task.Delay(20);

        await cts.CancelAsync();
        await loop;

        Assert.True(Volatile.Read(ref polls) >= 1, "Expected dequeue attempt after resume + awaken");
    }

    private static QueueManager CreateManager(ConfigManager? config = null)
    {
        config ??= CreateDefaultConfig();

        var usenet = new UsenetStreamingClient(
            config,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());

        return QueueManager.CreateForTests(
            usenet,
            config,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate(),
            startLoop: false);
    }

    private static ConfigManager CreateDefaultConfig()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
        ]);
        return config;
    }
}
