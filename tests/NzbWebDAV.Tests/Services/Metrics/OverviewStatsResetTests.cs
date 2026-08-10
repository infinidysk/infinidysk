using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests.Services.Metrics;

public class OverviewStatsResetTests
{
    [Fact]
    public async Task WipeAsync_DeletesAllMetricsTables()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        db.SegmentFetches.Add(new SegmentFetch
        {
            At = now,
            Provider = "a",
            Status = SegmentFetch.FetchStatus.Ok,
        });
        db.ReadSessions.Add(new ReadSession
        {
            Id = Guid.NewGuid(),
            StartedAt = now,
            EndedAt = now,
            DurationMs = 1,
            Path = "/x",
        });
        db.MetricEvents.Add(new MetricEvent { At = now, Kind = "test" });
        db.ThroughputMinutes.Add(new ThroughputMinute { Minute = now, BytesServed = 1 });
        db.ProviderMinutes.Add(new ProviderMinute { Minute = now, Provider = "a", Articles = 1 });
        db.ProviderHourly.Add(new ProviderHourly { Hour = now, Provider = "a", Articles = 1 });
        db.FailoverMisses.Add(new FailoverMiss
        {
            At = now,
            FromProvider = "a",
            ToProvider = "b",
            Reason = SegmentFetch.FetchStatus.Missing,
        });
        db.FailoverHourly.Add(new FailoverHourly
        {
            Hour = now,
            FromProvider = "a",
            ToProvider = "b",
            Reason = SegmentFetch.FetchStatus.Missing,
            Count = 1,
        });
        db.CatalogueDaily.Add(new CatalogueDaily { Day = now, FileCount = 1 });
        await db.SaveChangesAsync();

        var deleted = await OverviewStatsReset.WipeAsync(db, CancellationToken.None);

        Assert.Equal(9, deleted);
        Assert.Equal(0, await db.SegmentFetches.CountAsync());
        Assert.Equal(0, await db.ReadSessions.CountAsync());
        Assert.Equal(0, await db.MetricEvents.CountAsync());
        Assert.Equal(0, await db.ThroughputMinutes.CountAsync());
        Assert.Equal(0, await db.ProviderMinutes.CountAsync());
        Assert.Equal(0, await db.ProviderHourly.CountAsync());
        Assert.Equal(0, await db.FailoverMisses.CountAsync());
        Assert.Equal(0, await db.FailoverHourly.CountAsync());
        Assert.Equal(0, await db.CatalogueDaily.CountAsync());
    }

    [Fact]
    public async Task WipeProviderAsync_DeletesOnlyTargetProviderRows()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const string target = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        db.SegmentFetches.AddRange(
            new SegmentFetch { At = now, Provider = target, Status = SegmentFetch.FetchStatus.Ok },
            new SegmentFetch { At = now, Provider = other, Status = SegmentFetch.FetchStatus.Ok });
        db.ProviderMinutes.AddRange(
            new ProviderMinute { Minute = now, Provider = target, Articles = 1 },
            new ProviderMinute { Minute = now, Provider = other, Articles = 2 });
        db.ProviderHourly.AddRange(
            new ProviderHourly { Hour = now, Provider = target, Articles = 1 },
            new ProviderHourly { Hour = now, Provider = other, Articles = 2 });
        db.MetricEvents.AddRange(
            new MetricEvent { At = now, Kind = "circuit", Tag1 = target, Tag2 = "open" },
            new MetricEvent { At = now, Kind = "circuit", Tag1 = other, Tag2 = "open" },
            new MetricEvent
            {
                At = now,
                Kind = MetricsWriter.FailoverSaveEventKind,
                Tag1 = target,
            },
            new MetricEvent
            {
                At = now,
                Kind = MetricsWriter.FailoverSaveEventKind,
                Tag1 = other,
            },
            new MetricEvent { At = now, Kind = "global" });
        db.FailoverMisses.AddRange(
            new FailoverMiss
            {
                At = now,
                FromProvider = target,
                ToProvider = other,
                Reason = SegmentFetch.FetchStatus.Missing,
            },
            new FailoverMiss
            {
                At = now,
                FromProvider = other,
                ToProvider = "cccccccccccccccccccccccccccccccc",
                Reason = SegmentFetch.FetchStatus.Missing,
            });
        db.FailoverHourly.AddRange(
            new FailoverHourly
            {
                Hour = now,
                FromProvider = target,
                ToProvider = other,
                Reason = SegmentFetch.FetchStatus.Missing,
                Count = 1,
            },
            new FailoverHourly
            {
                Hour = now,
                FromProvider = other,
                ToProvider = "cccccccccccccccccccccccccccccccc",
                Reason = SegmentFetch.FetchStatus.Missing,
                Count = 2,
            });
        db.ThroughputMinutes.Add(new ThroughputMinute { Minute = now, BytesServed = 42 });
        db.ReadSessions.Add(new ReadSession
        {
            Id = Guid.NewGuid(),
            StartedAt = now,
            EndedAt = now,
            DurationMs = 1,
            Path = "/kept",
        });
        await db.SaveChangesAsync();

        var deleted = await OverviewStatsReset.WipeProviderAsync(db, target, CancellationToken.None);

        Assert.True(deleted >= 5);
        Assert.Equal(0, await db.SegmentFetches.CountAsync(x => x.Provider == target));
        Assert.Equal(1, await db.SegmentFetches.CountAsync(x => x.Provider == other));
        Assert.Equal(0, await db.ProviderMinutes.CountAsync(x => x.Provider == target));
        Assert.Equal(1, await db.ProviderMinutes.CountAsync(x => x.Provider == other));
        Assert.Equal(0, await db.ProviderHourly.CountAsync(x => x.Provider == target));
        Assert.Equal(1, await db.ProviderHourly.CountAsync(x => x.Provider == other));
        Assert.Equal(0, await db.MetricEvents.CountAsync(x =>
            x.Kind == "circuit" && x.Tag1 == target));
        Assert.Equal(0, await db.MetricEvents.CountAsync(x =>
            x.Kind == MetricsWriter.FailoverSaveEventKind && x.Tag1 == target));
        Assert.Equal(3, await db.MetricEvents.CountAsync());
        Assert.Equal(0, await db.FailoverMisses.CountAsync(x =>
            x.FromProvider == target || x.ToProvider == target));
        Assert.Equal(1, await db.FailoverMisses.CountAsync());
        Assert.Equal(0, await db.FailoverHourly.CountAsync(x =>
            x.FromProvider == target || x.ToProvider == target));
        Assert.Equal(1, await db.FailoverHourly.CountAsync());
        Assert.Equal(1, await db.ThroughputMinutes.CountAsync());
        Assert.Equal(1, await db.ReadSessions.CountAsync());
    }

    [Fact]
    public async Task WipeProviderAsync_DrainsSegmentFetchesInBatches()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var db = harness.Context;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const string target = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        var previous = OverviewStatsReset.SegmentFetchDeleteBatchSize;
        OverviewStatsReset.SegmentFetchDeleteBatchSize = 2;
        try
        {
            for (var i = 0; i < 5; i++)
            {
                db.SegmentFetches.Add(new SegmentFetch
                {
                    At = now + i,
                    Provider = target,
                    Status = SegmentFetch.FetchStatus.Ok,
                });
            }
            db.SegmentFetches.Add(new SegmentFetch
            {
                At = now,
                Provider = other,
                Status = SegmentFetch.FetchStatus.Ok,
            });
            await db.SaveChangesAsync();

            var deleted = await OverviewStatsReset.WipeProviderAsync(db, target, CancellationToken.None);

            Assert.Equal(5, deleted);
            Assert.Equal(0, await db.SegmentFetches.CountAsync(x => x.Provider == target));
            Assert.Equal(1, await db.SegmentFetches.CountAsync(x => x.Provider == other));
        }
        finally
        {
            OverviewStatsReset.SegmentFetchDeleteBatchSize = previous;
        }
    }

    [Fact]
    public void FoldUsageIntoOffsets_FoldsSnapshotAndOffset()
    {
        var providerId = Guid.NewGuid();
        var key = UsenetProviderIdentity.MetricsKey(providerId);
        var tracker = new ProviderBytesTracker();
        tracker.Add(key, 1_000);
        var config = new UsenetProviderConfig
        {
            Providers =
            [
                MakeProvider(providerId, bytesUsedOffset: 250, bytesUsedResetAt: 10),
            ],
        };

        var snapshot = OverviewStatsReset.SnapshotUsage(config, tracker);
        tracker.ResetCounters(); // fold must use the snapshot, not the cleared tracker

        var changed = OverviewStatsReset.FoldUsageIntoOffsets(config, snapshot, nowMs: 99);

        Assert.True(changed);
        Assert.Equal(1_250, config.Providers[0].BytesUsedOffset);
        Assert.Equal(99, config.Providers[0].BytesUsedResetAt);
    }

    [Fact]
    public void FoldUsageIntoOffsets_ScopedToProviderKey()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var aKey = UsenetProviderIdentity.MetricsKey(aId);
        var bKey = UsenetProviderIdentity.MetricsKey(bId);
        var tracker = new ProviderBytesTracker();
        tracker.Add(aKey, 100);
        tracker.Add(bKey, 200);
        var config = new UsenetProviderConfig
        {
            Providers =
            [
                MakeProvider(aId, bytesUsedOffset: 0, bytesUsedResetAt: 1),
                MakeProvider(bId, bytesUsedOffset: 5, bytesUsedResetAt: 1),
            ],
        };

        var snapshot = OverviewStatsReset.SnapshotUsage(config, tracker, providerKey: aKey);
        var changed = OverviewStatsReset.FoldUsageIntoOffsets(config, snapshot, nowMs: 50, providerKey: aKey);

        Assert.True(changed);
        Assert.Equal(100, config.Providers[0].BytesUsedOffset);
        Assert.Equal(50, config.Providers[0].BytesUsedResetAt);
        Assert.Equal(5, config.Providers[1].BytesUsedOffset);
        Assert.Equal(1, config.Providers[1].BytesUsedResetAt);
    }

    [Fact]
    public void FoldUsageIntoOffsets_SkipsEmptyProviderIdAndUnchanged()
    {
        var tracker = new ProviderBytesTracker();
        var config = new UsenetProviderConfig
        {
            Providers =
            [
                MakeProvider(Guid.Empty, bytesUsedOffset: 0, bytesUsedResetAt: 0),
            ],
        };

        var emptySnapshot = OverviewStatsReset.SnapshotUsage(config, tracker);
        Assert.Empty(emptySnapshot);
        Assert.False(OverviewStatsReset.FoldUsageIntoOffsets(config, emptySnapshot, nowMs: 1));

        var providerId = Guid.NewGuid();
        config = new UsenetProviderConfig
        {
            Providers =
            [
                MakeProvider(providerId, bytesUsedOffset: 0, bytesUsedResetAt: 42),
            ],
        };
        var snapshot = OverviewStatsReset.SnapshotUsage(config, tracker);
        Assert.False(OverviewStatsReset.FoldUsageIntoOffsets(config, snapshot, nowMs: 42));
    }

    [Fact]
    public void ProviderBytesTracker_ResetCounters_ClearsLifetimeAndBuckets()
    {
        var tracker = new ProviderBytesTracker();
        tracker.Add("a", 100);
        tracker.Add("b", 50);
        tracker.RecordSegmentThroughput("a", 1000, 10);

        tracker.ResetCounters();

        Assert.Equal(0, tracker.LifetimeAll);
        Assert.Equal(0, tracker.GetLifetime("a"));
        Assert.Equal(0, tracker.GetLifetime("b"));
        Assert.Empty(tracker.DrainClosed(long.MaxValue));
        Assert.True(tracker.GetBytesPerMs("a") > 0);
    }

    [Fact]
    public void ProviderBytesTracker_ResetProvider_RemovesOnlyTarget()
    {
        var tracker = new ProviderBytesTracker();
        tracker.Add("a", 100);
        tracker.Add("b", 50);
        tracker.RecordSegmentThroughput("a", 1000, 10);

        tracker.ResetProvider("a");

        Assert.Equal(50, tracker.LifetimeAll);
        Assert.Equal(0, tracker.GetLifetime("a"));
        Assert.Equal(50, tracker.GetLifetime("b"));
        Assert.True(tracker.GetBytesPerMs("a") > 0);
        Assert.DoesNotContain(tracker.DrainClosed(long.MaxValue), x => x.ProviderKey == "a");
    }

    [Fact]
    public void MetricsWriter_DiscardQueuedAndResetStats_ClearsQueuesAndDrops()
    {
        var invalidParent = Path.GetTempFileName();
        try
        {
            var options = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={Path.Join(invalidParent, "metrics.sqlite")}")
                .Options;
            var writer = new MetricsWriter(() => new MetricsDbContext(options));
            writer.RecordFetch(new SegmentFetch
            {
                At = 1,
                Provider = "a",
                Status = SegmentFetch.FetchStatus.Ok,
            });
            writer.RecordFailoverMiss(new FailoverMiss
            {
                At = 1,
                FromProvider = "a",
                ToProvider = "b",
                Reason = SegmentFetch.FetchStatus.Missing,
            });

            writer.DiscardQueuedAndResetStats();

            Assert.Equal(0, writer.Stats.QueuedFetches);
            Assert.Equal(0, writer.Stats.QueuedFailoverMisses);
            Assert.Equal(0, writer.Stats.DroppedFetches);
            Assert.Null(writer.Stats.LastFlushError);
        }
        finally
        {
            File.Delete(invalidParent);
        }
    }

    [Fact]
    public void MetricsWriter_DiscardQueuedForProvider_KeepsOtherProviders()
    {
        var invalidParent = Path.GetTempFileName();
        try
        {
            var options = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={Path.Join(invalidParent, "metrics.sqlite")}")
                .Options;
            var writer = new MetricsWriter(() => new MetricsDbContext(options));
            writer.RecordFetch(new SegmentFetch
            {
                At = 1,
                Provider = "a",
                Status = SegmentFetch.FetchStatus.Ok,
            });
            writer.RecordFetch(new SegmentFetch
            {
                At = 2,
                Provider = "b",
                Status = SegmentFetch.FetchStatus.Ok,
            });
            writer.RecordFailoverMiss(new FailoverMiss
            {
                At = 1,
                FromProvider = "a",
                ToProvider = "b",
                Reason = SegmentFetch.FetchStatus.Missing,
            });
            writer.RecordFailoverMiss(new FailoverMiss
            {
                At = 2,
                FromProvider = "b",
                ToProvider = "c",
                Reason = SegmentFetch.FetchStatus.Missing,
            });
            writer.RecordEvent(new MetricEvent
            {
                At = 1,
                Kind = MetricsWriter.FailoverSaveEventKind,
                Tag1 = "a",
            });
            writer.RecordEvent(new MetricEvent
            {
                At = 2,
                Kind = MetricsWriter.FailoverSaveEventKind,
                Tag1 = "b",
            });

            writer.DiscardQueuedForProvider("a");

            Assert.Equal(1, writer.Stats.QueuedFetches);
            Assert.Equal(1, writer.Stats.QueuedFailoverMisses);
            var saveEvents = writer.SnapshotQueuedEvents(MetricsWriter.FailoverSaveEventKind);
            var saveEvent = Assert.Single(saveEvents);
            Assert.Equal("b", saveEvent.Tag1);
        }
        finally
        {
            File.Delete(invalidParent);
        }
    }

    [Fact]
    public async Task MetricsWriter_BeginReset_DropsQueuedWithoutWriting()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var writer = new MetricsWriter(harness.CreateContext);
        writer.RecordFetch(new SegmentFetch
        {
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Provider = "a",
            Status = SegmentFetch.FetchStatus.Ok,
        });

        writer.BeginReset();
        try
        {
            await writer.FlushNowAsync();
            Assert.Equal(0, writer.Stats.QueuedFetches);
            await using var check = harness.CreateContext();
            Assert.Equal(0, await check.SegmentFetches.CountAsync());
        }
        finally
        {
            writer.EndReset();
        }
    }

    [Fact]
    public async Task MetricsWriter_BeginReset_AbandonsFailedFlushBatchWithoutRequeue()
    {
        var flushEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;

        var writer = new MetricsWriter(() =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                flushEntered.TrySetResult();
                allowFail.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException("simulated flush failure during reset");
            }

            throw new InvalidOperationException("unexpected second context create");
        });

        writer.RecordFetch(new SegmentFetch
        {
            At = 1,
            Provider = "a",
            Status = SegmentFetch.FetchStatus.Ok,
        });

        // FlushNowAsync invokes the context factory synchronously before the first
        // await, so run it off-thread or the test would deadlock on allowFail.
        var flushTask = Task.Run(() => writer.FlushNowAsync());
        await flushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        writer.BeginReset();
        allowFail.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => flushTask);
        Assert.Equal(0, writer.Stats.QueuedFetches);

        writer.DiscardQueuedAndResetStats();
        writer.EndReset();
    }

    [Fact]
    public async Task MetricsWriter_EndReset_AllowsFlushToResume()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var writer = new MetricsWriter(harness.CreateContext);

        writer.BeginReset();
        writer.RecordFetch(new SegmentFetch
        {
            At = 1,
            Provider = "a",
            Status = SegmentFetch.FetchStatus.Ok,
        });
        await writer.FlushNowAsync(); // drops while resetting
        Assert.Equal(0, writer.Stats.QueuedFetches);
        writer.EndReset();

        writer.RecordFetch(new SegmentFetch
        {
            At = 2,
            Provider = "a",
            Status = SegmentFetch.FetchStatus.Ok,
        });
        await writer.FlushNowAsync();

        await using var check = harness.CreateContext();
        Assert.Equal(1, await check.SegmentFetches.CountAsync());
        Assert.Equal(2, (await check.SegmentFetches.SingleAsync()).At);
    }

    private static UsenetProviderConfig.ConnectionDetails MakeProvider(
        Guid providerId,
        long bytesUsedOffset = 0,
        long bytesUsedResetAt = 0)
    {
        return new UsenetProviderConfig.ConnectionDetails
        {
            ProviderId = providerId,
            Type = ProviderType.Pooled,
            Host = "news.example.com",
            Port = 563,
            UseSsl = true,
            User = "user",
            Pass = "pass",
            MaxConnections = 10,
            BytesUsedOffset = bytesUsedOffset,
            BytesUsedResetAt = bytesUsedResetAt,
        };
    }

    private sealed class MetricsHarness : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly DbContextOptions<MetricsDbContext> _options;

        private MetricsHarness(string dir, DbContextOptions<MetricsDbContext> options, MetricsDbContext context)
        {
            _dir = dir;
            _options = options;
            Context = context;
        }

        public MetricsDbContext Context { get; }

        public MetricsDbContext CreateContext() => new(_options);

        public static async Task<MetricsHarness> CreateAsync()
        {
            var dir = Path.Join(Path.GetTempPath(), $"nzbdav-overview-reset-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var path = Path.Join(dir, "metrics.sqlite");
            var options = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new SqliteMetricsPragmas())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            var context = new MetricsDbContext(options);
            await context.Database.MigrateAsync();
            return new MetricsHarness(dir, options, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
