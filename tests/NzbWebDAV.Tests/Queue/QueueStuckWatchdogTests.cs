using System.Reflection;
using System.Text.Json;
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
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Queue;

[Collection(nameof(ConfigPathCollection))]
public sealed class QueueStuckWatchdogTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-stuck-wd-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private ConfigManager _configManager = null!;
    private QueueManager _queueManager = null!;
    private ProviderUsageTracker _queueManagerUsageTracker = null!;

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

        await using (var ctx = new DavDatabaseContext(_options))
            await ctx.Database.MigrateAsync();

        _configManager = new ConfigManager();
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
                {
                    Providers =
                    [
                        new UsenetProviderConfig.ConnectionDetails
                        {
                            ProviderId = Guid.NewGuid(),
                            Type = NzbWebDAV.Models.ProviderType.Pooled,
                            Host = "nntp.example",
                            Port = 563,
                            UseSsl = true,
                            User = "u",
                            Pass = "p",
                            MaxConnections = 20,
                        },
                    ],
                }),
            },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxQueueConnections, ConfigValue = "10" },
            new ConfigItem { ConfigName = ConfigKeys.QueueWorkerCount, ConfigValue = "1" },
        ]);

        var usenet = new UsenetStreamingClient(
            _configManager,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());

        _queueManagerUsageTracker = new ProviderUsageTracker();
        _queueManager = new QueueManager(
            usenet,
            _configManager,
            new WebsocketManager(),
            _queueManagerUsageTracker,
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate(),
            startLoop: false)
        {
            CreateDbContextOverride = () => new DavDatabaseContext(_options),
            StuckItemCheckInterval = TimeSpan.FromMilliseconds(50),
            StuckItemThreshold = TimeSpan.FromMilliseconds(250),
            StuckCancelGracePeriod = TimeSpan.FromMilliseconds(400),
        };
    }

    private void WireStallingClaimOverride(StallStream stall)
    {
        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (claimed, stall);
        };
    }

    // Each claim returns a FRESH stall stream (matching production, where every
    // claim reads a fresh NZB blob stream) and captures it so the test can bind
    // the current worker's CTS. Reusing one stream across claims breaks because
    // it stays bound to the previous worker's disposed CTS.
    private void WireStallingClaimOverridePerClaim(Action<StallStream> onClaimed)
    {
        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            var stall = new StallStream();
            onClaimed(stall);
            return (claimed, stall);
        };
    }

    public Task DisposeAsync()
    {
        _queueManager.Dispose();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try
        {
            if (Directory.Exists(_configRoot))
                Directory.Delete(_configRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task StuckProgress_CancelsWorkerAndSetsPauseUntil()
    {
        var stall = new StallStream();
        var item = CreateQueueItem("stuck.nzb", "movies", "StuckJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (claimed, stall);
        };

        var pauseWindowStarted = DateTime.Now;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        object? inProgress = null;
        while (DateTime.UtcNow < deadline)
        {
            inProgress = FindInProgressItem(item.Id);
            if (inProgress is not null) break;
            await Task.Delay(20);
        }

        Assert.NotNull(inProgress);
        var workerCts = GetWorkerCts(inProgress!);
        stall.BindWorker(workerCts);

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !workerCts.IsCancellationRequested)
            await Task.Delay(20);

        Assert.True(workerCts.IsCancellationRequested);

        DateTime? pauseUntil = null;
        while (DateTime.UtcNow < deadline)
        {
            await using var ctx = new DavDatabaseContext(_options);
            pauseUntil = await ctx.QueueItems.AsNoTracking()
                .Where(q => q.Id == item.Id)
                .Select(q => q.PauseUntil)
                .FirstOrDefaultAsync();
            if (pauseUntil is not null) break;
            await Task.Delay(20);
        }

        Assert.NotNull(pauseUntil);
        Assert.InRange(
            pauseUntil!.Value,
            pauseWindowStarted + TimeSpan.FromMinutes(14),
            pauseWindowStarted + TimeSpan.FromMinutes(21));

        await using (var ctx = new DavDatabaseContext(_options))
        {
            Assert.Equal(0, await ctx.HistoryItems.CountAsync());
            Assert.Equal(1, await ctx.QueueItems.CountAsync());
        }

        await cts.CancelAsync();
        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // ProcessQueueAsync may still be inside GetTopQueueItem when the loop token
            // is cancelled; shutdown cancellation is expected once assertions pass.
        }
    }

    [Fact]
    public async Task ProgressingItem_IsNotCancelledByWatchdog()
    {
        var stall = new StallStream();
        var item = CreateQueueItem("progress.nzb", "movies", "ProgressJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (claimed, stall);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        object? inProgress = null;
        while (DateTime.UtcNow < deadline)
        {
            inProgress = FindInProgressItem(item.Id);
            if (inProgress is not null) break;
            await Task.Delay(20);
        }

        Assert.NotNull(inProgress);
        stall.BindWorker(GetWorkerCts(inProgress!));

        using var progressCts = new CancellationTokenSource();
        var bumpTask = Task.Run(async () =>
        {
            var value = 1;
            while (!progressCts.Token.IsCancellationRequested)
            {
                SetProgressPercentage(inProgress!, value);
                value = value >= 90 ? 1 : value + 10;
                await Task.Delay(80, progressCts.Token);
            }
        }, progressCts.Token);

        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.False(GetWorkerCts(inProgress!).IsCancellationRequested);

        await using (var ctx = new DavDatabaseContext(_options))
        {
            var pauseUntil = await ctx.QueueItems.AsNoTracking()
                .Where(q => q.Id == item.Id)
                .Select(q => q.PauseUntil)
                .FirstAsync();
            Assert.Null(pauseUntil);
        }

        await progressCts.CancelAsync();
        try { await bumpTask; }
        catch (OperationCanceledException) { }

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FetchingButSilentItem_IsNotCancelledByWatchdog()
    {
        // A long stage (e.g. a large PAR2 descriptor walk) reports no progress
        // but keeps fetching articles. The watchdog must treat segment fetches
        // as liveness and leave the worker alone.
        var stall = new StallStream();
        var item = CreateQueueItem("fetching.nzb", "movies", "FetchingJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (claimed, stall);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        object? inProgress = null;
        while (DateTime.UtcNow < deadline)
        {
            inProgress = FindInProgressItem(item.Id);
            if (inProgress is not null) break;
            await Task.Delay(20);
        }

        Assert.NotNull(inProgress);
        stall.BindWorker(GetWorkerCts(inProgress!));

        // Freeze the percentage but keep recording segment fetches for this
        // queue item, mimicking a silent-but-working stage.
        using var fetchCts = new CancellationTokenSource();
        var fetchTask = Task.Run(async () =>
        {
            while (!fetchCts.Token.IsCancellationRequested)
            {
                RecordSegmentFetch(item.Id);
                await Task.Delay(60, fetchCts.Token);
            }
        }, fetchCts.Token);

        // Well past the 250ms threshold with zero percentage movement.
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.False(GetWorkerCts(inProgress!).IsCancellationRequested);

        await using (var ctx = new DavDatabaseContext(_options))
        {
            var pauseUntil = await ctx.QueueItems.AsNoTracking()
                .Where(q => q.Id == item.Id)
                .Select(q => q.PauseUntil)
                .FirstAsync();
            Assert.Null(pauseUntil);
        }

        await fetchCts.CancelAsync();
        try { await fetchTask; }
        catch (OperationCanceledException) { /* expected: fetch loop cancelled */ }

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SilentAndIdleItem_IsCancelledByWatchdog()
    {
        // Neither progress nor segment fetches: a genuinely wedged worker.
        var stall = new StallStream();
        var item = CreateQueueItem("idle.nzb", "movies", "IdleJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (claimed, stall);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        object? inProgress = null;
        while (DateTime.UtcNow < deadline)
        {
            inProgress = FindInProgressItem(item.Id);
            if (inProgress is not null) break;
            await Task.Delay(20);
        }

        Assert.NotNull(inProgress);
        stall.BindWorker(GetWorkerCts(inProgress!));

        // No progress, no fetches — the watchdog should still pause+cancel.
        // PauseUntil is written before CancelAsync, so poll for cancellation
        // (the terminal action) rather than asserting both after one observation.
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var workerCts = GetWorkerCts(inProgress!);
        while (DateTime.UtcNow < deadline && !workerCts.IsCancellationRequested)
            await Task.Delay(20);

        Assert.True(workerCts.IsCancellationRequested);

        await using (var ctx = new DavDatabaseContext(_options))
        {
            var pauseUntil = await ctx.QueueItems.AsNoTracking()
                .Where(q => q.Id == item.Id)
                .Select(q => q.PauseUntil)
                .FirstOrDefaultAsync();
            Assert.NotNull(pauseUntil);
        }

        await cts.CancelAsync();
        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // ProcessQueueAsync may still be inside GetTopQueueItem when the loop token
            // is cancelled; shutdown cancellation is expected once assertions pass.
        }
    }

    [Fact]
    public async Task EarlyStalls_PauseAndRetry_ItemStaysQueued()
    {
        // Stall attempts 1 and 2 (MaxStuckAttempts = 3) must keep the item queued
        // with PauseUntil set and must NOT write history.
        var item = CreateQueueItem("retry.nzb", "movies", "RetryJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        // Each claim gets a fresh stall stream; bind it to its worker's CTS.
        var claimed = new TaskCompletionSource<StallStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        WireStallingClaimOverridePerClaim(s => claimed.TrySetResult(s));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            var stall = await claimed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            claimed = new TaskCompletionSource<StallStream>(TaskCreationOptions.RunContinuationsAsynchronously);

            object? inProgress = await WaitForInProgress(item.Id, TimeSpan.FromSeconds(10));
            Assert.NotNull(inProgress);
            var workerCts = GetWorkerCts(inProgress!);
            stall.BindWorker(workerCts);

            // Wait for the watchdog to cancel this worker.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && !workerCts.IsCancellationRequested)
                await Task.Delay(20);
            Assert.True(workerCts.IsCancellationRequested, $"stall cycle {cycle}: worker was not cancelled");

            // Wait for the coordinator to reap the cancelled worker.
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && FindInProgressItem(item.Id) is not null)
                await Task.Delay(20);

            // Still queued, still no history.
            await using (var ctx = new DavDatabaseContext(_options))
            {
                Assert.Equal(1, await ctx.QueueItems.CountAsync());
                Assert.Equal(0, await ctx.HistoryItems.CountAsync());
            }

            // Clear PauseUntil so the next cycle can claim it, and wake the
            // coordinator (which may be idle-sleeping up to a minute).
            await using (var ctx = new DavDatabaseContext(_options))
                await ctx.QueueItems.ExecuteUpdateAsync(s => s.SetProperty(q => q.PauseUntil, (DateTime?)null));
            _queueManager.AwakenQueue();
        }

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ThirdStall_FailsItemIntoHistory()
    {
        var item = CreateQueueItem("stall3.nzb", "movies", "StallThreeJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        var claimed = new TaskCompletionSource<StallStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        WireStallingClaimOverridePerClaim(s => claimed.TrySetResult(s));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var stall = await claimed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            claimed = new TaskCompletionSource<StallStream>(TaskCreationOptions.RunContinuationsAsynchronously);

            object? inProgress = await WaitForInProgress(item.Id, TimeSpan.FromSeconds(15));
            Assert.NotNull(inProgress);
            var workerCts = GetWorkerCts(inProgress!);
            stall.BindWorker(workerCts);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline && !workerCts.IsCancellationRequested)
                await Task.Delay(20);
            Assert.True(workerCts.IsCancellationRequested, $"stall cycle {cycle}: worker was not cancelled");

            // Wait for reap.
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline && FindInProgressItem(item.Id) is not null)
                await Task.Delay(20);

            if (cycle < 2)
            {
                // Retries 1 and 2: still queued, no history.
                await using (var ctx = new DavDatabaseContext(_options))
                {
                    Assert.Equal(1, await ctx.QueueItems.CountAsync());
                    Assert.Equal(0, await ctx.HistoryItems.CountAsync());
                }
                await using (var ctx = new DavDatabaseContext(_options))
                    await ctx.QueueItems.ExecuteUpdateAsync(s => s.SetProperty(q => q.PauseUntil, (DateTime?)null));
                _queueManager.AwakenQueue();
            }
        }

        // Final stall: item must have failed into history.
        await using (var ctx = new DavDatabaseContext(_options))
        {
            Assert.Equal(0, await ctx.QueueItems.CountAsync());
            var history = await ctx.HistoryItems.SingleAsync();
            Assert.Equal(HistoryItem.DownloadStatusOption.Failed, history.DownloadStatus);
            Assert.False(string.IsNullOrWhiteSpace(history.FailMessage));
        }

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelWithoutFailFlag_LeavesItemQueued_NoHistory()
    {
        // A user-initiated cancel (no FailOnStuckCancel flag) must keep the item
        // queued and write no history — only the watchdog's final stall may fail.
        var item = CreateQueueItem("usercancel.nzb", "movies", "UserCancelJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        var claimed = new TaskCompletionSource<StallStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        WireStallingClaimOverridePerClaim(s => claimed.TrySetResult(s));

        // Push the watchdog threshold beyond the test window so the watchdog never
        // fires — this isolates the plain (non-watchdog) cancellation path.
        _queueManager.StuckItemThreshold = TimeSpan.FromHours(1);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);

        var stall = await claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        object? inProgress = await WaitForInProgress(item.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(inProgress);
        stall.BindWorker(GetWorkerCts(inProgress!));

        // Simulate a user-initiated cancel directly on the worker CTS (bypasses
        // the watchdog, so FailOnStuckCancel stays false). Immediately stop the
        // coordinator so it cannot re-claim the still-queued item with a fresh
        // worker (which would stall a stream bound to no CTS and hang shutdown).
        await GetWorkerCts(inProgress!).CancelAsync();
        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var ctx = new DavDatabaseContext(_options))
        {
            Assert.Equal(1, await ctx.QueueItems.CountAsync());
            Assert.Equal(0, await ctx.HistoryItems.CountAsync());
        }
    }

    [Fact]
    public async Task WorkerIgnoringCancellation_LogsError_AndKeepsSlot()
    {
        // A worker that never observes cancellation occupies its slot until the
        // underlying I/O is released. The ignored-cancel observer must log an
        // Error after the grace period without abandoning the slot.
        var hung = new HungStream();
        var item = CreateQueueItem("hung.nzb", "movies", "HungJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (claimed, hung);
        };

        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Error()
            .WriteTo.Sink(sink)
            .CreateLogger();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);
        try
        {
            object? inProgress = await WaitForInProgress(item.Id, TimeSpan.FromSeconds(5));
            Assert.NotNull(inProgress);

            var workerCts = GetWorkerCts(inProgress!);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && !workerCts.IsCancellationRequested)
                await Task.Delay(20);
            Assert.True(workerCts.IsCancellationRequested);

            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline &&
                   !sink.Events.Any(e => e.Level == LogEventLevel.Error
                       && e.RenderMessage().Contains("ignored cancellation", StringComparison.OrdinalIgnoreCase)))
            {
                await Task.Delay(20);
            }

            Assert.Contains(sink.Events, e =>
                e.Level == LogEventLevel.Error
                && e.RenderMessage().Contains("ignored cancellation", StringComparison.OrdinalIgnoreCase)
                && e.RenderMessage().Contains("HungJob", StringComparison.Ordinal));
            Assert.NotNull(FindInProgressItem(item.Id));
            Assert.False(GetProcessingTask(inProgress!).IsCompleted);

            await using (var ctx = new DavDatabaseContext(_options))
            {
                Assert.Equal(1, await ctx.QueueItems.CountAsync());
                Assert.Equal(0, await ctx.HistoryItems.CountAsync());
            }
        }
        finally
        {
            hung.Release();
            await cts.CancelAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task RemoveQueueItemsAsync_HungWorker_QuarantinesAndReturnsWithinGrace()
    {
        // A worker ignoring cancellation must not hang a SAB delete: the call
        // returns after the grace period with the id flagged still-running, the
        // row stays queued, and the slot stays occupied until the task stops.
        _queueManager.StuckItemThreshold = TimeSpan.FromHours(1);
        var hung = new HungStream();
        var item = CreateQueueItem("hung-remove.nzb", "movies", "HungRemoveJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (claimed, hung);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);
        try
        {
            object? inProgress = await WaitForInProgress(item.Id, TimeSpan.FromSeconds(5));
            Assert.NotNull(inProgress);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<Guid> stillRunning;
            await using (var ctx = new DavDatabaseContext(_options))
            {
                stillRunning = await _queueManager.RemoveQueueItemsAsync(
                    [item.Id], new DavDatabaseClient(ctx), CancellationToken.None);
            }

            stopwatch.Stop();

            Assert.Equal([item.Id], stillRunning.ToArray());
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"remove hung for {stopwatch.Elapsed}; expected a bounded grace wait");

            // Quarantined: row kept, slot still occupied, no counters cleared.
            Assert.NotNull(FindInProgressItem(item.Id));
            Assert.False(GetProcessingTask(inProgress!).IsCompleted);
            await using (var ctx = new DavDatabaseContext(_options))
            {
                Assert.Equal(1, await ctx.QueueItems.CountAsync());
                Assert.Equal(0, await ctx.HistoryItems.CountAsync());
            }

            // Stop re-claims before releasing: the row stays queued and claimable,
            // and a released HungStream reads as EOF, which would fail a re-claimed
            // worker into history and break the assertions below.
            _queueManager.GetTopQueueItemOverride = (_, _) =>
                Task.FromResult<(QueueItem? queueItem, Stream? queueNzbStream)>((null, null));

            // Releasing the hung I/O lets the cancelled worker stop; the shutdown
            // path then reaps it. The row remains queued for a later claim.
            hung.Release();
            await cts.CancelAsync();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Expected when shutdown cancels an in-flight claim.
            }

            Assert.Null(FindInProgressItem(item.Id));
            await using (var ctx = new DavDatabaseContext(_options))
            {
                Assert.Equal(1, await ctx.QueueItems.CountAsync());
                Assert.Equal(0, await ctx.HistoryItems.CountAsync());
            }
        }
        finally
        {
            hung.Release();
            await cts.CancelAsync();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Expected when shutdown cancels an in-flight claim.
            }
        }
    }

    [Fact]
    public async Task Shutdown_HungWorker_LogsErrorAndReturnsWithinGrace()
    {
        _queueManager.StuckItemThreshold = TimeSpan.FromHours(1);
        var hung = new HungStream();
        var item = CreateQueueItem("hung-shutdown.nzb", "movies", "HungShutdownJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (claimed, hung);
        };

        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Error()
            .WriteTo.Sink(sink)
            .CreateLogger();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);
        try
        {
            object? inProgress = await WaitForInProgress(item.Id, TimeSpan.FromSeconds(5));
            Assert.NotNull(inProgress);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await cts.CancelAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(10));
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"shutdown hung for {stopwatch.Elapsed}; expected a bounded grace wait");
            Assert.Contains(sink.Events, e =>
                e.Level == LogEventLevel.Error
                && e.RenderMessage().Contains("ignored cancellation", StringComparison.OrdinalIgnoreCase));

            // Quarantined workers keep their slot and are not reaped.
            Assert.NotNull(FindInProgressItem(item.Id));
        }
        finally
        {
            hung.Release();
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task ReplaceExistingQueueItem_HungWorker_FailsSubmissionInsteadOfInserting()
    {
        _queueManager.StuckItemThreshold = TimeSpan.FromHours(1);
        var hung = new HungStream();
        var item = CreateQueueItem("replace-hung.nzb", "movies", "ReplaceHungJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (claimed, hung);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);
        try
        {
            object? inProgress = await WaitForInProgress(item.Id, TimeSpan.FromSeconds(5));
            Assert.NotNull(inProgress);

            var controller = new global::NzbWebDAV.Api.SabControllers.AddFile.AddFileController(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                new DavDatabaseClient(new DavDatabaseContext(_options)),
                _queueManager,
                _configManager,
                new WebsocketManager());

            var nzb = """
                <?xml version="1.0" encoding="utf-8"?>
                <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
                  <file subject="test">
                    <groups><group>alt.binaries.test</group></groups>
                    <segments>
                      <segment bytes="100" number="1">seg@example.com</segment>
                    </segments>
                  </file>
                </nzb>
                """;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<Microsoft.AspNetCore.Http.BadHttpRequestException>(() =>
                controller.AddFileAsync(new global::NzbWebDAV.Api.SabControllers.AddFile.AddFileRequest
                {
                    ReplaceExistingQueueItem = true,
                    FileName = "replace-hung.nzb",
                    ContentType = "application/x-nzb",
                    NzbFileStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(nzb)),
                    Category = "movies",
                    Priority = QueueItem.PriorityOption.Normal,
                    PostProcessing = QueueItem.PostProcessingOption.None,
                    CancellationToken = CancellationToken.None,
                }));
            stopwatch.Stop();

            Assert.Contains("still stopping", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"replace hung for {stopwatch.Elapsed}; expected a bounded grace wait");

            // The quarantined row survives and no replacement row was inserted.
            await using (var ctx = new DavDatabaseContext(_options))
            {
                var rows = await ctx.QueueItems.AsNoTracking().ToListAsync();
                Assert.Single(rows);
                Assert.Equal(item.Id, rows[0].Id);
            }
        }
        finally
        {
            // Block re-claims before releasing the hung stream so teardown cannot
            // start a fresh worker that reads EOF off the released stream.
            _queueManager.GetTopQueueItemOverride = (_, _) =>
                Task.FromResult<(QueueItem? queueItem, Stream? queueNzbStream)>((null, null));
            hung.Release();
            await cts.CancelAsync();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Expected when shutdown cancels an in-flight claim.
            }
        }
    }

    [Fact]
    public async Task RemoveQueueItemsAsync_TwoHungWorkers_ShareOneGraceBudget()
    {
        // Two hung workers must be bounded by one shared grace period, not
        // N × grace: cancel all first, then wait on a single deadline.
        _queueManager.StuckItemThreshold = TimeSpan.FromHours(1);
        _queueManager.StuckCancelGracePeriod = TimeSpan.FromSeconds(2);
        _configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.QueueWorkerCount, ConfigValue = "2" },
        ]);

        var hung1 = new HungStream();
        var hung2 = new HungStream();
        var item1 = CreateQueueItem("hung-a.nzb", "movies", "HungJobA");
        var item2 = CreateQueueItem("hung-b.nzb", "movies", "HungJobB");
        item2.CreatedAt = item1.CreatedAt.AddMinutes(1);

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.AddRange(item1, item2);
            await ctx.SaveChangesAsync();
        }

        var claims = new Queue<HungStream>([hung1, hung2]);
        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (claimed, claims.Count > 0 ? claims.Dequeue() : new HungStream());
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);
        try
        {
            Assert.NotNull(await WaitForInProgress(item1.Id, TimeSpan.FromSeconds(5)));
            Assert.NotNull(await WaitForInProgress(item2.Id, TimeSpan.FromSeconds(5)));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<Guid> stillRunning;
            await using (var ctx = new DavDatabaseContext(_options))
            {
                stillRunning = await _queueManager.RemoveQueueItemsAsync(
                    [item1.Id, item2.Id], new DavDatabaseClient(ctx), CancellationToken.None);
            }

            stopwatch.Stop();

            Assert.Equal(2, stillRunning.Count);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(3500),
                $"two hung workers took {stopwatch.Elapsed}; one shared 2s grace budget " +
                "should bound the wait, per-worker budgets would take ~4s");
        }
        finally
        {
            _queueManager.GetTopQueueItemOverride = (_, _) =>
                Task.FromResult<(QueueItem? queueItem, Stream? queueNzbStream)>((null, null));
            hung1.Release();
            hung2.Release();
            await cts.CancelAsync();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Expected when shutdown cancels an in-flight claim.
            }
        }
    }

    private async Task<object?> WaitForInProgress(Guid queueItemId, TimeSpan timeout, QueueManager? manager = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = FindInProgressItem(queueItemId, manager);
            if (found is not null) return found;
            await Task.Delay(20);
        }
        return null;
    }


    private static Task GetProcessingTask(object inProgressItem)
    {
        var prop = inProgressItem.GetType().GetProperty(
            "ProcessingTask",
            BindingFlags.Instance | BindingFlags.Public);
        return (Task)prop!.GetValue(inProgressItem)!;
    }

    [Fact]
    public async Task HealthyItems_DoNotWaitForWatchdogThreshold()
    {
        var item1 = CreateQueueItem("fast1.nzb", "movies", "FastJob1");
        var item2 = CreateQueueItem("fast2.nzb", "movies", "FastJob2");
        item1.CreatedAt = DateTime.Now.AddMinutes(-2);
        item2.CreatedAt = DateTime.Now.AddMinutes(-1);

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.AddRange(item1, item2);
            await ctx.SaveChangesAsync();
        }

        var item1CompleteTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item2ClaimedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTime item1CompleteAt = default;
        DateTime item2ClaimedAt = default;
        using var gate1 = new ManualResetEventSlim(true);
        using var gate2 = new ManualResetEventSlim(false);

        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();

            if (claimed.Id == item1.Id)
            {
                return (claimed, new ObservedGateStream(gate1, () =>
                {
                    item1CompleteAt = DateTime.UtcNow;
                    item1CompleteTcs.TrySetResult();
                }));
            }

            if (claimed.Id == item2.Id)
            {
                item2ClaimedAt = DateTime.UtcNow;
                item2ClaimedTcs.TrySetResult();
                return (claimed, new GateStream(gate2));
            }

            return (claimed, new GateStream(gate2));
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);

        try
        {
            await item1CompleteTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await item2ClaimedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var gap = item2ClaimedAt - item1CompleteAt;
            Assert.True(
                gap < TimeSpan.FromMilliseconds(500),
                $"Second item claimed {gap.TotalMilliseconds:F0}ms after first completed; expected prompt claim");

            var item2InProgress = await WaitForInProgress(item2.Id, TimeSpan.FromSeconds(5));
            Assert.NotNull(item2InProgress);
            gate2.Set();
            await GetProcessingTask(item2InProgress!).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate2.Set();
            await cts.CancelAsync();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Expected when shutdown cancels GetTopQueueItem.
            }
        }
    }

    private object? FindInProgressItem(Guid queueItemId, QueueManager? manager = null)
    {
        manager ??= _queueManager;
        var field = typeof(QueueManager).GetField(
            "_inProgress",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var dict = field!.GetValue(manager)!;
        var args = new object?[] { queueItemId, null };
        var found = (bool)dict.GetType().GetMethod("TryGetValue")!.Invoke(dict, args)!;
        return found ? args[1] : null;
    }

    private static CancellationTokenSource GetWorkerCts(object inProgressItem)
    {
        var prop = inProgressItem.GetType().GetProperty(
            "CancellationTokenSource",
            BindingFlags.Instance | BindingFlags.Public);
        return (CancellationTokenSource)prop!.GetValue(inProgressItem)!;
    }

    private static void SetProgressPercentage(object inProgressItem, int value)
    {
        var prop = inProgressItem.GetType().GetProperty(
            "ProgressPercentage",
            BindingFlags.Instance | BindingFlags.Public);
        prop!.SetValue(inProgressItem, value);
    }

    private void RecordSegmentFetch(Guid queueItemId)
    {
        using var scope = _queueManagerUsageTracker.BeginScope(queueItemId);
        _queueManagerUsageTracker.RecordSuccess("nntp.example");
    }

    private static QueueItem CreateQueueItem(string fileName, string category, string jobName)
    {
        return new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.Now,
            FileName = fileName,
            JobName = jobName,
            NzbFileSize = 100,
            TotalSegmentBytes = 200,
            Category = category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };
    }

    /// <summary>
    /// Blocks reads until the bound worker CTS is cancelled (simulates a cooperative hang).
    /// </summary>
    private sealed class StallStream : Stream
    {
        private volatile CancellationTokenSource? _workerCts;

        public void BindWorker(CancellationTokenSource workerCts) => _workerCts = workerCts;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var workerCts = _workerCts;
                if (workerCts is not null && workerCts.IsCancellationRequested)
                    throw new OperationCanceledException(workerCts.Token);
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Blocks reads until <see cref="Release"/> is called and ignores every
    /// cancellation token, simulating a worker whose underlying I/O does not
    /// observe the worker CTS. Releasing lets the test dispose the manager.
    /// </summary>
    private sealed class HungStream : Stream
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetCanceled();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _release.Task.ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events) return _events.ToArray();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }

    /// <summary>
    /// GateStream that invokes a callback after the payload is read once.
    /// </summary>
    private sealed class ObservedGateStream(ManualResetEventSlim gate, Action onComplete) : Stream
    {
        private readonly GateStream _inner = new(gate);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                onComplete();
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
