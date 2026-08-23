using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class MemoryBudgetTests
{
    private const long Mb = 1024 * 1024;

    [Theory]
    [InlineData(64 * Mb, 64)]    // floored
    [InlineData(256 * Mb, 64)]   // 25% of 256 = 64, at floor
    [InlineData(512 * Mb, 128)]  // 25% of 512
    [InlineData(1024 * Mb, 256)]
    [InlineData(2048 * Mb, 512)]
    [InlineData(4096 * Mb, 1024)]
    [InlineData(16384 * Mb, 4096)]
    [InlineData(32768 * Mb, 8192)]
    [InlineData(65536 * Mb, 8192)]
    public void DefaultInFlightArticleBudgetMb_ScalesWithHeap(long heap, int expected)
    {
        Assert.Equal(expected, MemoryBudget.DefaultInFlightArticleBudgetMb(heap));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DefaultInFlightArticleBudgetMb_UsesFallbackForInvalidHeapLimit(long heap)
    {
        Assert.Equal(128, MemoryBudget.DefaultInFlightArticleBudgetMb(heap));
    }

    [Fact]
    public void DetectedHeapLimit_IsPositive()
    {
        Assert.True(MemoryBudget.HeapLimitBytes > 0);
    }
}
