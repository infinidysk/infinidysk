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
public sealed class ConcurrentQueueManagerTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-qworkers-cfg-{Guid.NewGuid():N}");
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
            new ConfigItem { ConfigName = ConfigKeys.QueueWorkerCount, ConfigValue = "2" },
        ]);

        var usenet = new UsenetStreamingClient(
            _configManager,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());

        _queueManager = QueueManager.CreateForTests(
            usenet,
            _configManager,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate(),
            startLoop: false);
        _queueManager.CreateDbContextOverride = () => new DavDatabaseContext(_options);
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
    public async Task ProcessQueueAsync_WithWorkerCountTwo_RunsTwoItemsConcurrently()
    {
        using var gate = new ManualResetEventSlim(false);
        var item1 = CreateQueueItem("a.nzb", "movies", "JobA");
        var item2 = CreateQueueItem("b.nzb", "movies", "JobB");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.AddRange(item1, item2);
            await ctx.SaveChangesAsync();
        }

        // Fresh streams per claim so each worker blocks independently.
        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (item, _) = await client.GetTopQueueItem(exclude, ct);
            if (item is null) return (null, null);
            // Detach so the worker context owns the entity instance.
            ctx.ChangeTracker.Clear();
            return (item, new GateStream(gate));
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var maxActive = 0;
        while (DateTime.UtcNow < deadline)
        {
            maxActive = Math.Max(maxActive, _queueManager.GetInProgressQueueItems().Count);
            if (maxActive >= 2) break;
            await Task.Delay(20);
        }

        Assert.True(maxActive >= 2, $"Expected 2 concurrent workers, saw max {maxActive}");
        Assert.True(_queueManager.HasActiveQueueItems);
        Assert.NotNull(_queueManager.FindInProgressQueueItem(item1.Id)
            ?? _queueManager.FindInProgressQueueItem(item2.Id));

        gate.Set();

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && _queueManager.HasActiveQueueItems)
            await Task.Delay(20);

        await cts.CancelAsync();
        try { await loop; }
        catch (OperationCanceledException) { }

        await using (var ctx = new DavDatabaseContext(_options))
        {
            Assert.Empty(await ctx.QueueItems.AsNoTracking().ToListAsync());
            Assert.True(await ctx.HistoryItems.CountAsync() >= 2);
        }
    }

    [Fact]
    public async Task ProcessQueueAsync_SkipsSameMountKeyWhileSiblingIsActive()
    {
        using var gate = new ManualResetEventSlim(false);
        var first = CreateQueueItem("dup1.nzb", "tv", "SameJob");
        var sibling = CreateQueueItem("dup2.nzb", "tv", "SameJob");
        var other = CreateQueueItem("other.nzb", "tv", "OtherJob");
        first.CreatedAt = DateTime.Now.AddMinutes(-3);
        sibling.CreatedAt = DateTime.Now.AddMinutes(-2);
        other.CreatedAt = DateTime.Now.AddMinutes(-1);

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.AddRange(first, sibling, other);
            await ctx.SaveChangesAsync();
        }

        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (item, _) = await client.GetTopQueueItem(exclude, ct);
            if (item is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (item, new GateStream(gate));
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);

        var sawConflictPair = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var active = _queueManager.GetInProgressQueueItems();
            var names = active.Select(x => x.QueueItem.JobName).ToHashSet();
            if (names.Contains("SameJob") && names.Contains("OtherJob"))
                sawConflictPair = true;
            if (active.Count(x => x.QueueItem.JobName == "SameJob") > 1)
                Assert.Fail("Two SameJob items were processing concurrently");
            if (sawConflictPair && active.Count >= 2) break;
            await Task.Delay(20);
        }

        Assert.True(sawConflictPair,
            "Expected SameJob and OtherJob to overlap while the duplicate SameJob waited");
        Assert.Null(_queueManager.FindInProgressQueueItem(sibling.Id));

        gate.Set();
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && _queueManager.HasActiveQueueItems)
            await Task.Delay(20);

        await cts.CancelAsync();
        try { await loop; }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ProcessQueueAsync_DoesNotSpinAfterAwakenWhileWorkerRuns()
    {
        using var gate = new ManualResetEventSlim(false);
        var item = CreateQueueItem("spin.nzb", "movies", "SpinJob");

        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.QueueItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        // One claimable item, so once it is in flight every later poll returns
        // null and each poll marks one pass of the coordinator loop.
        var polls = 0;
        _queueManager.GetTopQueueItemOverride = async (exclude, ct) =>
        {
            Interlocked.Increment(ref polls);
            await using var ctx = new DavDatabaseContext(_options);
            var client = new DavDatabaseClient(ctx);
            var (claimed, _) = await client.GetTopQueueItem(exclude, ct);
            if (claimed is null) return (null, null);
            ctx.ChangeTracker.Clear();
            return (claimed, new GateStream(gate));
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = _queueManager.ProcessQueueAsync(cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !_queueManager.HasActiveQueueItems)
            await Task.Delay(20);
        Assert.True(_queueManager.HasActiveQueueItems, "Expected a worker to be in flight");

        // An addfile lands while that worker is still running. The signal must be
        // consumed once; latching it spins the loop as fast as the CPU allows.
        var before = Volatile.Read(ref polls);
        _queueManager.AwakenQueue();
        await Task.Delay(500);
        var passes = Volatile.Read(ref polls) - before;

        Assert.True(passes <= 15,
            $"Coordinator ran {passes} passes in 500ms, expected a bounded poll rather than a spin");

        gate.Set();
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && _queueManager.HasActiveQueueItems)
            await Task.Delay(20);

        await cts.CancelAsync();
        try { await loop; }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task GetTopQueueItem_ExcludesInFlightIds()
    {
        var a = CreateQueueItem("a.nzb", "movies", "A");
        var b = CreateQueueItem("b.nzb", "movies", "B");
        a.CreatedAt = DateTime.Now.AddMinutes(-2);
        b.CreatedAt = DateTime.Now.AddMinutes(-1);

        await using var ctx = new DavDatabaseContext(_options);
        var client = new DavDatabaseClient(ctx);
        ctx.QueueItems.AddRange(a, b);
        await ctx.SaveChangesAsync();

        var (top, _) = await client.GetTopQueueItem([a.Id]);
        Assert.NotNull(top);
        Assert.Equal(b.Id, top.Id);
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
}
