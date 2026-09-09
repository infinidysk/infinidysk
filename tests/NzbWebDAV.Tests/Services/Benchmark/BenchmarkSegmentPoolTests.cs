using NzbWebDAV.Services.Benchmark;

namespace NzbWebDAV.Tests.Services.Benchmark;

public class BenchmarkSegmentPoolTests
{
    [Fact]
    public void TryNext_ReturnsIdsInRoundRobinOrder()
    {
        var pool = new BenchmarkSegmentPool(["a", "b", "c"]);

        var actual = Enumerable.Range(0, 4).Select(_ => Next(pool)).ToList();

        Assert.Equal(["a", "b", "c", "a"], actual);
    }

    [Fact]
    public void MarkDead_AdvancesLogicalSegmentToFallback()
    {
        var pool = new BenchmarkSegmentPool(
        [
            new BenchmarkSegment("a", ["a-fallback"]),
            new BenchmarkSegment("b", []),
        ]);

        pool.MarkDead("a");

        Assert.Equal("a-fallback", Next(pool));
        Assert.Equal("b", Next(pool));
        Assert.Equal(0, pool.DeadCount);
    }

    [Fact]
    public void TryNext_WhenAllCandidatesAreDead_ReturnsFalse()
    {
        var pool = new BenchmarkSegmentPool(
        [
            new BenchmarkSegment("a", ["a-fallback"]),
            new BenchmarkSegment("b", []),
        ]);
        pool.MarkDead("a");
        pool.MarkDead("a-fallback");
        pool.MarkDead("b");

        Assert.False(pool.TryNext(out _));
        Assert.True(pool.Exhausted);
        Assert.Equal(2, pool.DeadCount);
    }

    [Fact]
    public void NextBatch_StopsAtExhaustionInsteadOfPaddingDeadIds()
    {
        var pool = new BenchmarkSegmentPool(
        [
            new BenchmarkSegment("a", ["a-fallback"]),
            new BenchmarkSegment("b", []),
        ]);
        pool.MarkDead("b");

        var batch = pool.NextBatch(8);

        Assert.NotEmpty(batch);
        Assert.DoesNotContain("b", batch);

        pool.MarkDead("a");
        pool.MarkDead("a-fallback");

        Assert.Empty(pool.NextBatch(8));
    }

    [Fact]
    public void Constructor_DuplicateCandidateIdsUseLastLogicalSegment()
    {
        var pool = new BenchmarkSegmentPool(
        [
            new BenchmarkSegment("first", ["shared"]),
            new BenchmarkSegment("second", ["shared"]),
        ]);

        pool.MarkRetrieved("shared");
        pool.MarkRetrieved("second");

        Assert.True(pool.WrappedAround);
    }

    [Fact]
    public void StringConstructor_DuplicateIdsDoNotThrow()
    {
        var pool = new BenchmarkSegmentPool(["duplicate", "duplicate"]);

        Assert.Equal("duplicate", Next(pool));
    }

    [Fact]
    public void WrappedAround_TracksSuccessfulReuseRatherThanSelections()
    {
        var pool = new BenchmarkSegmentPool(["a"]);

        Assert.Equal("a", Next(pool));
        Assert.Equal("a", Next(pool));
        Assert.False(pool.WrappedAround);

        pool.MarkRetrieved("a");
        Assert.False(pool.WrappedAround);
        pool.MarkRetrieved("a");
        Assert.True(pool.WrappedAround);
    }

    [Fact]
    public void WrappedAround_TreatsFallbackAsSameLogicalSegment()
    {
        var pool = new BenchmarkSegmentPool([new BenchmarkSegment("a", ["a-fallback"])]);

        pool.MarkRetrieved("a");
        pool.MarkRetrieved("a-fallback");

        Assert.True(pool.WrappedAround);
    }

    private static string Next(BenchmarkSegmentPool pool)
    {
        Assert.True(pool.TryNext(out var id));
        return id;
    }
}
