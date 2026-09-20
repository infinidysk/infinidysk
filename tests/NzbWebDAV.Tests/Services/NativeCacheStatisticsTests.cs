using NzbWebDAV.Services.NativeCache;
using System.Text.Json;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeCacheStatisticsTests
{
    [Fact]
    public void ConcurrentCounters_AreBoundedAggregatesWithoutPathsOrMediaIdentity()
    {
        var statistics = new NativeCacheStatistics();
        Parallel.For(0, 1000, _ =>
        {
            statistics.Hit(4);
            statistics.Miss();
            statistics.Committed(8);
            statistics.Fallback(timeout: true);
        });
        var snapshot = statistics.Snapshot();
        Assert.Equal(1000, snapshot.HitBlocks);
        Assert.Equal(4000, snapshot.HitBytes);
        Assert.Equal(1000, snapshot.MissBlocks);
        Assert.Equal(8000, snapshot.CommittedBytes);
        Assert.Equal(1000, snapshot.Fallbacks);
        Assert.Equal(1000, snapshot.IoTimeouts);
        Assert.DoesNotContain("path", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }
}
