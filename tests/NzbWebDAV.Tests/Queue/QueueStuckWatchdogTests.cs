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

        _queueManager = new QueueManager(
            usenet,
            _configManager,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate(),
            startLoop: false)
        {
            CreateDbContextOverride = () => new DavDatabaseContext(_options),
            StuckItemCheckInterval = TimeSpan.FromMilliseconds(50),
            StuckItemThreshold = TimeSpan.FromMilliseconds(250),
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

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
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
        var now = DateTime.Now;
        Assert.InRange(pauseUntil!.Value, now + TimeSpan.FromMinutes(14), now + TimeSpan.FromMinutes(21));

        await using (var ctx = new DavDatabaseContext(_options))
        {
            Assert.Equal(0, await ctx.HistoryItems.CountAsync());
            Assert.Equal(1, await ctx.QueueItems.CountAsync());
        }

        Assert.True(GetWorkerCts(inProgress!).IsCancellationRequested);

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
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
        var gate1 = new ManualResetEventSlim(true);
        var gate2 = new ManualResetEventSlim(true);

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

        await item1CompleteTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await item2ClaimedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var gap = item2ClaimedAt - item1CompleteAt;
        Assert.True(
            gap < TimeSpan.FromMilliseconds(500),
            $"Second item claimed {gap.TotalMilliseconds:F0}ms after first completed; expected prompt claim");

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private object? FindInProgressItem(Guid queueItemId)
    {
        var field = typeof(QueueManager).GetField(
            "_inProgress",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var dict = field!.GetValue(_queueManager)!;
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
