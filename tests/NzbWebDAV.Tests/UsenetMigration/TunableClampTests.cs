using NzbWebDAV.UsenetMigration;

namespace NzbWebDAV.Tests.UsenetMigration;

public class TunableClampTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    [InlineData(-5, 1)]
    public void ClampMaxQueueDepth(int input, int expected) =>
        Assert.Equal(expected, UsenetMigrationStore.ClampMaxQueueDepth(input));

    [Theory]
    [InlineData(0, 20, 1)]
    [InlineData(1, 20, 1)]
    [InlineData(16, 20, 16)]
    [InlineData(17, 20, 16)]
    [InlineData(8, 4, 4)] // workers cannot exceed depth
    public void ClampSubmitWorkers(int workers, int depth, int expected) =>
        Assert.Equal(expected, UsenetMigrationStore.ClampSubmitWorkers(workers, depth));
}
