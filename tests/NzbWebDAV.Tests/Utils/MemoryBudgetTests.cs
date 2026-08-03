using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class MemoryBudgetTests
{
    private const long Mb = 1024 * 1024;

    [Theory]
    [InlineData(256 * Mb, 64)]   // 25% of 256 = 64, at floor
    [InlineData(512 * Mb, 128)]  // 25% of 512
    [InlineData(1024 * Mb, 256)]
    [InlineData(2048 * Mb, 512)] // 25% would be 512, at ceiling
    [InlineData(4096 * Mb, 512)] // capped at previous fixed default
    [InlineData(64 * Mb, 64)]    // floored
    public void DefaultInFlightArticleBudgetMb_ScalesWithHeap(long heap, int expected)
    {
        Assert.Equal(expected, MemoryBudget.DefaultInFlightArticleBudgetMb(heap));
    }

    [Fact]
    public void DetectedHeapLimit_IsPositive()
    {
        Assert.True(MemoryBudget.HeapLimitBytes > 0);
    }
}
