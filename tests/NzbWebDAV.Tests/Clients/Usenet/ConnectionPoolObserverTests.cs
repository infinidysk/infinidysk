using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Logging;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class ConnectionPoolObserverTests : IDisposable
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

    public ConnectionPoolObserverTests()
        => SynchronousObserverInvoker.ResetFailureLogThrottleForTests();

    public void Dispose()
        => SynchronousObserverInvoker.ResetFailureLogThrottleForTests();

    [Fact]
    public async Task Borrow_ThrowingFirstStatsSubscriber_ReturnsLockAndInvokesLaterSubscriber()
    {
        await using var pool = CreatePool();
        var order = new List<string>();
        AttachThrowingThenCounting(pool, order, throwEnabled: () => true);

        var borrowed = await pool.GetConnectionLockAsync(SemaphorePriority.High)
            .WaitAsync(WaitBudget);
        Assert.Equal(["first", "second"], order);
        Assert.NotNull(borrowed.Connection);

        borrowed.Dispose();
        var again = await pool.GetConnectionLockAsync(SemaphorePriority.High)
            .WaitAsync(WaitBudget);
        again.Dispose();
    }

    [Fact]
    public async Task Return_ThrowingFirstStatsSubscriber_DoesNotFaultLockDispose()
    {
        await using var pool = CreatePool();
        var order = new List<string>();
        var throwEnabled = false;
        AttachThrowingThenCounting(pool, order, () => throwEnabled);

        var borrowed = await pool.GetConnectionLockAsync(SemaphorePriority.High)
            .WaitAsync(WaitBudget);
        order.Clear();
        throwEnabled = true;

        borrowed.Dispose();

        Assert.Equal(["first", "second"], order);
        Assert.Equal(1, pool.IdleConnections);
    }

    [Fact]
    public async Task Destroy_ThrowingFirstStatsSubscriber_DoesNotFaultLockDispose()
    {
        await using var pool = CreatePool();
        var order = new List<string>();
        var throwEnabled = false;
        AttachThrowingThenCounting(pool, order, () => throwEnabled);

        var borrowed = await pool.GetConnectionLockAsync(SemaphorePriority.High)
            .WaitAsync(WaitBudget);
        borrowed.Replace("test replacement");
        order.Clear();
        throwEnabled = true;

        borrowed.Dispose();

        Assert.Equal(["first", "second"], order);
        Assert.Equal(1, pool.GetChurn().ConnectionsDestroyed);

        var replacement = await pool.GetConnectionLockAsync(SemaphorePriority.High)
            .WaitAsync(WaitBudget);
        replacement.Dispose();
        Assert.Equal(2, pool.GetChurn().ConnectionsOpened);
    }

    [Fact]
    public async Task Sweeper_ThrowingStatsSubscriber_CompletesAndCanSweepAgain()
    {
        await using var pool = CreatePool();
        var order = new List<string>();
        AttachThrowingThenCounting(pool, order, throwEnabled: () => true);

        var borrowed = await pool.GetConnectionLockAsync(SemaphorePriority.High)
            .WaitAsync(WaitBudget);
        borrowed.Dispose();
        order.Clear();

        await pool.SweepOnceForTestsAsync(
            nowMillis: Environment.TickCount64 + (long)pool.IdleTimeout.TotalMilliseconds + 1)
            .WaitAsync(WaitBudget);
        Assert.Equal(["first", "second"], order);

        await pool.SweepOnceForTestsAsync(
            nowMillis: Environment.TickCount64 + (long)pool.IdleTimeout.TotalMilliseconds + 1)
            .WaitAsync(WaitBudget);
    }

    private static ConnectionPool<object> CreatePool() =>
        new(
            maxConnections: 1,
            _ => ValueTask.FromResult(new object()),
            TimeSpan.FromMinutes(5));

    private static void AttachThrowingThenCounting(
        ConnectionPool<object> pool,
        List<string> order,
        Func<bool> throwEnabled)
    {
        pool.OnConnectionPoolChanged += (_, _) =>
        {
            order.Add("first");
            if (throwEnabled())
                throw new InvalidOperationException("stats observer");
        };
        pool.OnConnectionPoolChanged += (_, _) => order.Add("second");
    }
}
