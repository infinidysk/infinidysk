namespace NzbWebDAV.Tests.TestUtils;

public sealed class BarrierThreadsTests
{
    [Fact]
    public void Run_MarshalsWorkerExceptionsToCaller()
    {
        var ex = Assert.Throws<AggregateException>(() =>
            BarrierThreads.Run(2, i =>
            {
                if (i == 0)
                    throw new InvalidOperationException("worker-0");
            }));

        Assert.Contains(ex.InnerExceptions, inner => inner is InvalidOperationException);
    }
}
