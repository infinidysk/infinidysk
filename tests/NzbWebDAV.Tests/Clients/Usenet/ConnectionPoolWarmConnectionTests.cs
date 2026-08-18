using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ConnectionPoolWarmConnectionTests
{
    [Fact]
    public async Task Startup_PrewarmesFloorWithoutRetainingBorrowerPermits()
    {
        var created = 0;
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 3,
            connectionFactory: _ => ValueTask.FromResult(new TestConnection(Interlocked.Increment(ref created))),
            idleTimeout: TimeSpan.FromMinutes(1),
            warmConnectionFloor: 2);

        await WaitUntilAsync(() => pool.LiveConnections == 2 && pool.IdleConnections == 2);

        var locks = new List<ConnectionLock<TestConnection>>();
        for (var i = 0; i < 3; i++)
            locks.Add(await pool.GetConnectionLockAsync(SemaphorePriority.High).WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(3, pool.ActiveConnections);
        Assert.Equal(3, created);

        foreach (var connectionLock in locks)
            connectionLock.Dispose();
    }

    [Fact]
    public async Task Sweeper_KeepsExpiredFloorAndPingsIt()
    {
        var keepAliveCalls = 0;
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 3,
            connectionFactory: _ => ValueTask.FromResult(new TestConnection(0)),
            idleTimeout: TimeSpan.FromMinutes(1),
            warmConnectionFloor: 2,
            keepAlive: (_, _) =>
            {
                Interlocked.Increment(ref keepAliveCalls);
                return Task.CompletedTask;
            });

        await WaitUntilAsync(() => pool.LiveConnections == 2 && pool.IdleConnections == 2);
        await pool.SweepOnceForTestsAsync(
            nowMillis: Environment.TickCount64 + (long)pool.IdleTimeout.TotalMilliseconds + 1);

        Assert.Equal(2, pool.LiveConnections);
        Assert.Equal(2, pool.IdleConnections);
        Assert.Equal(2, keepAliveCalls);
    }

    [Fact]
    public async Task FailedIdleKeepAlive_RecyclesConnectionAndRefillsFloor()
    {
        var first = new TestConnection(1) { FailKeepAlive = true };
        var created = 0;
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 1,
            connectionFactory: _ => ValueTask.FromResult(
                Interlocked.Increment(ref created) == 1 ? first : new TestConnection(created)),
            idleTimeout: TimeSpan.FromMinutes(1),
            warmConnectionFloor: 1,
            keepAlive: (connection, _) => connection.FailKeepAlive
                ? Task.FromException(new IOException("idle socket closed"))
                : Task.CompletedTask);

        await WaitUntilAsync(() => pool.LiveConnections == 1 && pool.IdleConnections == 1);
        await pool.SweepOnceForTestsAsync();

        Assert.True(first.Disposed);
        Assert.Equal(2, created);
        Assert.Equal(1, pool.LiveConnections);
        Assert.Equal(1, pool.IdleConnections);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        await Task.WhenAny(
            Task.Run(async () =>
            {
                while (!condition())
                    await Task.Delay(10);
            }),
            Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(condition(), "Timed out waiting for connection-pool state.");
    }

    private sealed class TestConnection(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool FailKeepAlive { get; init; }
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
