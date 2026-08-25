using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public sealed class ZeroFillLogLimiterTests : IDisposable
{
    public ZeroFillLogLimiterTests() => ZeroFillLogLimiter.ResetForTests();

    public void Dispose() => ZeroFillLogLimiter.ResetForTests();

    [Fact]
    public void TryLog_CoalescesRepeatsPerFile()
    {
        var path = $"/view/limit-{Guid.NewGuid():N}.mkv";
        Assert.True(ZeroFillLogLimiter.TryLog(path, out var firstSuppressed));
        Assert.Equal(0, firstSuppressed);
        Assert.False(ZeroFillLogLimiter.TryLog(path, out _));
        Assert.False(ZeroFillLogLimiter.TryLog(path, out _));
    }

    [Fact]
    public void TryLog_AlwaysAllowsUnattributedAndUnknown()
    {
        Assert.True(ZeroFillLogLimiter.TryLog(null, out _));
        Assert.True(ZeroFillLogLimiter.TryLog("", out _));
        Assert.True(ZeroFillLogLimiter.TryLog("unknown", out _));
        Assert.True(ZeroFillLogLimiter.TryLog("unknown", out _));
    }

    [Fact]
    public void TryLog_DistinctPathsWithSameFileName_EachLogOnce()
    {
        // Fixed name: this class runs sequentially and resets the limiter per test,
        // so a deterministic fixture cannot leak windows between runs.
        const string name = "movie-same-name-distinct-dirs.mkv";
        Assert.True(ZeroFillLogLimiter.TryLog($"/view/a/{name}", out _));
        Assert.True(ZeroFillLogLimiter.TryLog($"/view/b/{name}", out _));
        Assert.False(ZeroFillLogLimiter.TryLog($"/view/a/{name}", out _));
        Assert.False(ZeroFillLogLimiter.TryLog($"/view/b/{name}", out _));
    }
}
