using NzbWebDAV.Clients.Usenet;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class SegmentCacheStatisticsTests
{
    [Fact]
    public async Task ConcurrentHits_RecordExactTotals()
    {
        var statistics = new SegmentCacheStatistics();
        const int tasks = 32;
        const int hitsPerTask = 1_000;
        const long bytesPerHit = 64;

        await Task.WhenAll(Enumerable.Range(0, tasks).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < hitsPerTask; i++)
                statistics.RecordHit(bytesPerHit);
        })));

        var snapshot = statistics.GetSnapshot();
        Assert.Equal(tasks * hitsPerTask, snapshot.Hits);
        Assert.Equal(tasks * hitsPerTask * bytesPerHit, snapshot.BytesServed);
        Assert.Null(snapshot.QueuedWriteBytes);
        Assert.Null(snapshot.PeakQueuedWriteBytes);
    }

    [Fact]
    public async Task SnapshotsDuringUpdates_NeverDecreaseProcessLifetimeCounters()
    {
        var statistics = new SegmentCacheStatistics();
        var decreasing = 0;
        using var cts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            var previousHits = 0L;
            var previousBytes = 0L;
            while (!cts.IsCancellationRequested)
            {
                var snapshot = statistics.GetSnapshot();
                if (snapshot.Hits < previousHits || snapshot.BytesServed < previousBytes)
                    Interlocked.Increment(ref decreasing);
                previousHits = snapshot.Hits;
                previousBytes = snapshot.BytesServed;
                await Task.Yield();
            }
        });

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                statistics.RecordHit(8);
        })));

        cts.Cancel();
        await reader;
        Assert.Equal(0, decreasing);
        Assert.Equal(16 * 500, statistics.GetSnapshot().Hits);
    }

    [Fact]
    public void LaterGeneration_SupersedesGauges_AndIgnoresStaleUpdates()
    {
        var statistics = new SegmentCacheStatistics();
        var generationA = statistics.BeginGeneration(enabled: true, maxBytes: 100);
        generationA.SetCatalogReady(12, entries: 3, currentBytes: 30);
        generationA.SetIndex(3, 30);

        var generationB = statistics.BeginGeneration(enabled: true, maxBytes: 200);
        generationB.SetCatalogReady(8, entries: 1, currentBytes: 10);

        generationA.SetIndex(99, 999);
        generationA.SetCatalogReady(50, 99, 999);

        var snapshot = statistics.GetSnapshot();
        Assert.True(snapshot.Enabled);
        Assert.True(snapshot.CatalogReady);
        Assert.Equal(8, snapshot.CatalogLoadDurationMs);
        Assert.Equal(1, snapshot.Entries);
        Assert.Equal(10, snapshot.CurrentBytes);
        Assert.Equal(200, snapshot.MaxBytes);
    }

    [Fact]
    public void LaterGeneration_IgnoresStaleWriterSnapshots()
    {
        var statistics = new SegmentCacheStatistics();
        var generationA = statistics.BeginGeneration(enabled: true, maxBytes: 100);
        generationA.SetWriterSnapshot(new SegmentCacheWriteBehindSnapshot(64, 32, 32, 1, 1, 0));

        var generationB = statistics.BeginGeneration(enabled: true, maxBytes: 200);
        generationB.SetWriterSnapshot(new SegmentCacheWriteBehindSnapshot(128, 16, 24, 2, 1, 3));
        generationA.SetWriterSnapshot(new SegmentCacheWriteBehindSnapshot(64, 64, 64, 9, 9, 9));

        var snapshot = Assert.IsType<SegmentCacheWriteBehindSnapshot>(
            statistics.GetWriterSnapshot());
        Assert.Equal(128, snapshot.BudgetBytes);
        Assert.Equal(16, snapshot.ReservedBytes);
        Assert.Equal(24, snapshot.PeakReservedBytes);
        Assert.Equal(2, snapshot.QueuedJobs);
        Assert.Equal(1, snapshot.ActiveJobs);
        Assert.Equal(3, snapshot.CapacitySkips);
    }

    [Fact]
    public void LateRetiredGeneration_StillContributesOutcomeCounters()
    {
        var statistics = new SegmentCacheStatistics();
        statistics.BeginGeneration(enabled: true, maxBytes: 100);
        statistics.RecordHit(16);
        statistics.BeginGeneration(enabled: true, maxBytes: 200);
        statistics.RecordHit(32);
        statistics.RecordMiss();

        var snapshot = statistics.GetSnapshot();
        Assert.Equal(2, snapshot.Hits);
        Assert.Equal(48, snapshot.BytesServed);
        Assert.Equal(1, snapshot.Misses);
        Assert.Equal(200, snapshot.MaxBytes);
    }

    [Fact]
    public void DisabledGeneration_HasZeroActiveGauges_AndPreservesCounters()
    {
        var statistics = new SegmentCacheStatistics();
        statistics.BeginGeneration(enabled: true, maxBytes: 50);
        statistics.RecordHit(8);
        statistics.BeginGeneration(enabled: false, maxBytes: 50);

        var snapshot = statistics.GetSnapshot();
        Assert.False(snapshot.Enabled);
        Assert.False(snapshot.CatalogReady);
        Assert.Null(snapshot.CatalogLoadDurationMs);
        Assert.Equal(0, snapshot.Entries);
        Assert.Equal(0, snapshot.CurrentBytes);
        Assert.Equal(0, snapshot.MaxBytes);
        Assert.Equal(1, snapshot.Hits);
        Assert.Equal(8, snapshot.BytesServed);
        Assert.Null(snapshot.QueuedWriteBytes);
        Assert.Null(snapshot.PeakQueuedWriteBytes);
    }

    [Fact]
    public void WriteAttempt_CompletesOnce()
    {
        var statistics = new SegmentCacheStatistics();
        var attempt = statistics.BeginWriteAttempt();
        attempt.Complete(SegmentCacheWriteOutcome.Committed, 10);
        attempt.Complete(SegmentCacheWriteOutcome.Failed, 10);

        var snapshot = statistics.GetSnapshot();
        Assert.Equal(1, snapshot.WriteAttempts);
        Assert.Equal(1, snapshot.WriteCommits);
        Assert.Equal(0, snapshot.WriteFailures);
    }
}
