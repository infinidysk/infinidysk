using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Services.Diagnostics;

public sealed class MemoryComponentSnapshotTests
{
    [Fact]
    public async Task Capture_UsesRawByteUnitsAndSeparatesLogicalArticleBytes()
    {
        var budget = new InFlightArticleBudget(10_000);
        using var lease = await budget.LeaseAsync(1_000, CancellationToken.None);
        budget.AccountBufferedPipeBytes(500);
        var builder = CreateBuilder(budget);

        var snapshot = builder.Capture();

        Assert.Equal(MemoryComponentSnapshotBuilder.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.NotEqual(default, snapshot.CapturedAtUtc);
        Assert.True(snapshot.MonotonicTimestamp > 0);
        Assert.True(snapshot.CaptureDurationMicroseconds >= 0);
        Assert.Equal(1_500, snapshot.InFlightArticles.TotalAccountedBytes);
        Assert.Equal(1_000, snapshot.InFlightArticles.ArticleDestinationLogicalBytes);
        Assert.Equal(500, snapshot.InFlightArticles.DecodedPipeBytes);
        Assert.True(snapshot.InFlightArticles.IsConsistent);
        Assert.False(snapshot.CacheWriter.Supported);
        Assert.Null(snapshot.CacheWriter.WriteBudgetBytes);
        Assert.Null(snapshot.CacheWriter.QueuedWriteBytes);

        budget.AccountBufferedPipeBytes(-500);
    }

    [Fact]
    public void Capture_QuiescentOwnersAreInternallyConsistent()
    {
        var snapshot = CreateBuilder(new InFlightArticleBudget(10_000)).Capture();

        Assert.Equal(0, snapshot.InFlightArticles.TotalAccountedBytes);
        Assert.Equal(0, snapshot.InFlightArticles.ArticleDestinationLogicalBytes);
        Assert.Equal(0, snapshot.InFlightArticles.DecodedPipeBytes);
        Assert.Equal(0, snapshot.InFlightArticles.WaiterCount);
        Assert.Equal(0, snapshot.Activity.ActiveReads);
        Assert.Equal(0, snapshot.Activity.CurrentInFlightSegmentFetches);
    }

    [Fact]
    public void Capture_ProjectsActiveCacheWriterOwnership()
    {
        var statistics = new SegmentCacheStatistics();
        var generation = statistics.BeginGeneration(enabled: true, maxBytes: 1024);
        generation.SetWriterSnapshot(new SegmentCacheWriteBehindSnapshot(
            BudgetBytes: 64,
            ReservedBytes: 32,
            PeakReservedBytes: 48,
            QueuedJobs: 2,
            ActiveJobs: 1,
            CapacitySkips: 3));

        var snapshot = CreateBuilder(new InFlightArticleBudget(10_000), statistics).Capture();

        Assert.True(snapshot.CacheWriter.Supported);
        Assert.Equal(64, snapshot.CacheWriter.WriteBudgetBytes);
        Assert.Equal(32, snapshot.CacheWriter.QueuedWriteBytes);
        Assert.Equal(48, snapshot.CacheWriter.PeakQueuedWriteBytes);
        Assert.Equal(2, snapshot.CacheWriter.QueuedJobs);
        Assert.Equal(1, snapshot.CacheWriter.ActiveJobs);
        Assert.Equal(3, snapshot.CacheWriter.CapacitySkipsTotal);
    }

    private static MemoryComponentSnapshotBuilder CreateBuilder(
        InFlightArticleBudget budget,
        SegmentCacheStatistics? statistics = null)
    {
        var config = new ConfigManager();
        return new MemoryComponentSnapshotBuilder(
            budget,
            config,
            new ConcurrentReadTracker(configManager: config),
            new ActiveReadRegistry(),
            statistics ?? new SegmentCacheStatistics());
    }
}
