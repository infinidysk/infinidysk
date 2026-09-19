using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Logging;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Events;

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
    public async Task Return_DebugSummaryIsBoundedAndCountsSuppressedReturns()
    {
        var sink = new CollectingLogEventSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var previousLogger = Log.Logger;
        Log.Logger = logger;
        try
        {
            var clock = new ReturnLogClock();
            await using var pool = new ConnectionPool<object>(1,
                _ => ValueTask.FromResult(new object()), timeProvider: clock);
            for (var index = 0; index < 100; index++)
            {
                using var borrowed = await pool.GetConnectionLockAsync(SemaphorePriority.High);
            }

            var first = Assert.Single(sink.Events, entry => entry.MessageTemplate.Text.StartsWith(
                "NNTP pool returns", StringComparison.Ordinal));
            Assert.Equal(1L, Assert.IsType<ScalarValue>(first.Properties["Returns"]).Value);

            clock.ElapsedMilliseconds = 30_000;
            using (await pool.GetConnectionLockAsync(SemaphorePriority.High)) { }

            var summaries = sink.Events.Where(entry => entry.MessageTemplate.Text.StartsWith(
                "NNTP pool returns", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, summaries.Length);
            Assert.Equal(100L, Assert.IsType<ScalarValue>(summaries[1].Properties["Returns"]).Value);
            Assert.Equal(1, pool.IdleConnections);
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    private sealed class ReturnLogClock : TimeProvider
    {
        internal long ElapsedMilliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ElapsedMilliseconds;
    }

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
