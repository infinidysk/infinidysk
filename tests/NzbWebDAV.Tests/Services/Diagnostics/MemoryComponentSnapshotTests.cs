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

    private static MemoryComponentSnapshotBuilder CreateBuilder(InFlightArticleBudget budget)
    {
        var config = new ConfigManager();
        return new MemoryComponentSnapshotBuilder(
            budget,
            config,
            new ConcurrentReadTracker(configManager: config),
            new ActiveReadRegistry(),
            new SegmentCacheStatistics());
    }
}
