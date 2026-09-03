using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ConnectionPoolWarmConnectionTests
{
    [Fact]
    public async Task WarmToAsync_OpensMissingConnectionsInParallel()
    {
        var entered = 0;
        var peak = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 5,
            connectionFactory: async ct =>
            {
                var active = Interlocked.Increment(ref entered);
                UpdateMaximum(ref peak, active);
                await release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref entered);
                return new TestConnection(active);
            },
            idleTimeout: TimeSpan.FromMinutes(1));

        var warming = pool.WarmToAsync(5);
        await WaitUntilAsync(() => Volatile.Read(ref entered) == 3);
        Assert.Equal(3, Volatile.Read(ref peak));
        Assert.Equal(3, pool.PendingConnectionCreations);

        release.TrySetResult();
        await warming.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(5, pool.LiveConnections);
        Assert.Equal(5, pool.IdleConnections);
        Assert.Equal(0, pool.PendingConnectionCreations);
    }

    [Fact]
    public async Task WarmToAsync_ExpandsPartlyWarmPoolWithoutExceedingTarget()
    {
        var created = 0;
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 8,
            connectionFactory: _ => ValueTask.FromResult(
                new TestConnection(Interlocked.Increment(ref created))),
            idleTimeout: TimeSpan.FromMinutes(1));

        await pool.WarmToAsync(1);
        await pool.WarmToAsync(5);

        Assert.Equal(5, created);
        Assert.Equal(5, pool.LiveConnections);
        Assert.Equal(5, pool.IdleConnections);
        Assert.Equal(0, pool.PendingConnectionCreations);
    }

    [Fact]
    public async Task WarmToAsync_TargetAbovePoolWidthStopsAtMaximum()
    {
        var created = 0;
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 3,
            connectionFactory: _ => ValueTask.FromResult(
                new TestConnection(Interlocked.Increment(ref created))),
            idleTimeout: TimeSpan.FromMinutes(1));

        await pool.WarmToAsync(20);

        Assert.Equal(3, created);
        Assert.Equal(3, pool.LiveConnections);
        Assert.Equal(3, pool.IdleConnections);
    }

    [Fact]
    public async Task WarmToAsync_ConcurrentBorrowerReusesPublishedConnection()
    {
        var factoryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 1,
            connectionFactory: async ct =>
            {
                factoryEntered.TrySetResult();
                await releaseFactory.Task.WaitAsync(ct);
                return new TestConnection(Interlocked.Increment(ref created));
            },
            idleTimeout: TimeSpan.FromMinutes(1));

        var warming = pool.WarmToAsync(1);
        await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var borrower = pool.GetConnectionLockAsync(SemaphorePriority.High);
        Assert.False(borrower.IsCompleted);

        releaseFactory.TrySetResult();
        await warming.WaitAsync(TimeSpan.FromSeconds(1));
        using var connection = await borrower.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, created);
        Assert.True(connection.WasReused);
        Assert.Equal(0, pool.PendingConnectionCreations);
    }

    [Fact]
    public async Task WarmToAsync_ConcurrentHintsCoalesceAtTarget()
    {
        var created = 0;
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 8,
            connectionFactory: async ct =>
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                return new TestConnection(Interlocked.Increment(ref created));
            },
            idleTimeout: TimeSpan.FromMinutes(1));

        await Task.WhenAll(pool.WarmToAsync(5), pool.WarmToAsync(5));

        Assert.Equal(5, created);
        Assert.Equal(5, pool.LiveConnections);
        Assert.Equal(0, pool.PendingConnectionCreations);
    }

    [Fact]
    public async Task WarmToAsync_FactoryFailureDoesNotSpinOrLeakReservation()
    {
        var attempts = 0;
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 1,
            connectionFactory: _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("provider unavailable");
            },
            idleTimeout: TimeSpan.FromMinutes(1));

        await pool.WarmToAsync(1).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, attempts);
        Assert.Equal(1, pool.GetChurn().HandshakeFailures);
        Assert.Equal(0, pool.PendingConnectionCreations);
        Assert.Equal(0, pool.LiveConnections);
    }

    [Fact]
    public async Task WarmToAsync_FirstFactoryFailureStopsNewStarts()
    {
        var started = 0;
        var allAdmitted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSuccesses = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 8,
            connectionFactory: async ct =>
            {
                var attempt = Interlocked.Increment(ref started);
                if (attempt == 3)
                    allAdmitted.TrySetResult();
                await allAdmitted.Task.WaitAsync(ct);
                if (attempt == 1)
                {
                    await releaseFailure.Task.WaitAsync(ct);
                    throw new IOException("provider unavailable");
                }
                await releaseSuccesses.Task.WaitAsync(ct);
                return new TestConnection(attempt);
            },
            idleTimeout: TimeSpan.FromMinutes(1));

        var warming = pool.WarmToAsync(8);
        await allAdmitted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseFailure.TrySetResult();
        await WaitUntilAsync(() => pool.GetChurn().HandshakeFailures == 1);
        releaseSuccesses.TrySetResult();
        await warming.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(3, started);
        Assert.Equal(2, pool.LiveConnections);
        Assert.Equal(0, pool.PendingConnectionCreations);
    }

    [Fact]
    public async Task WarmToAsync_CancellationReleasesReservations()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 1,
            connectionFactory: async ct =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new TestConnection(1);
            },
            idleTimeout: TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();

        var warming = pool.WarmToAsync(1, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await cancellation.CancelAsync();
        await warming.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, pool.PendingConnectionCreations);
        Assert.Equal(0, pool.LiveConnections);
    }

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
        var created = 0;
        var pingedIds = new ConcurrentBag<int>();
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 3,
            connectionFactory: _ => ValueTask.FromResult(
                new TestConnection(Interlocked.Increment(ref created))),
            idleTimeout: TimeSpan.FromMinutes(1),
            warmConnectionFloor: 2,
            keepAlive: (connection, _) =>
            {
                pingedIds.Add(connection.Id);
                return Task.CompletedTask;
            });

        await WaitUntilAsync(() => pool.LiveConnections == 2 && pool.IdleConnections == 2);
        await pool.SweepOnceForTestsAsync(
            nowMillis: Environment.TickCount64 + (long)pool.IdleTimeout.TotalMilliseconds + 1);

        Assert.Equal(2, pool.LiveConnections);
        Assert.Equal(2, pool.IdleConnections);
        Assert.Equal(2, pingedIds.Count);
        Assert.Equal(2, pingedIds.Distinct().Count());
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

    [Fact]
    public async Task InFlightKeepAliveReservesPhysicalPoolCapacity()
    {
        var created = 0;
        var keepAliveEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseKeepAlive = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 1,
            connectionFactory: _ => ValueTask.FromResult(
                new TestConnection(Interlocked.Increment(ref created))),
            idleTimeout: TimeSpan.FromMinutes(1),
            warmConnectionFloor: 1,
            keepAlive: async (_, ct) =>
            {
                keepAliveEntered.TrySetResult();
                await releaseKeepAlive.Task.WaitAsync(ct);
            });

        await WaitUntilAsync(() => pool.LiveConnections == 1 && pool.IdleConnections == 1);
        var sweep = pool.SweepOnceForTestsAsync();
        await keepAliveEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var borrower = pool.GetConnectionLockAsync(SemaphorePriority.High);
        Assert.False(borrower.IsCompleted);
        Assert.Equal(1, created);

        releaseKeepAlive.TrySetResult();
        await sweep.WaitAsync(TimeSpan.FromSeconds(1));
        using var connection = await borrower.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task KeepAliveAdmissionTimeoutDoesNotPinSweeper()
    {
        using var admission = new ProviderConnectionAdmission(
            getEffectiveProviderLimit: () => 1,
            configuredTransferLimit: 1);
        var keepAliveCalls = 0;
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 1,
            connectionFactory: _ => ValueTask.FromResult(new TestConnection(0)),
            idleTimeout: TimeSpan.FromMinutes(1),
            warmConnectionFloor: 1,
            keepAlive: (_, _) =>
            {
                Interlocked.Increment(ref keepAliveCalls);
                return Task.CompletedTask;
            },
            keepAliveAdmission: async ct => await admission.AcquireAsync(
                ProviderConnectionKind.Metadata,
                SemaphorePriority.Low,
                ct),
            keepAliveBorrowTimeout: TimeSpan.FromSeconds(1));

        await WaitUntilAsync(() => pool.LiveConnections == 1 && pool.IdleConnections == 1);
        using var transfer = await admission.AcquireAsync(
            ProviderConnectionKind.Transfer,
            SemaphorePriority.Low,
            CancellationToken.None);

        var sweep = pool.SweepOnceForTestsAsync();
        await WaitUntilAsync(
            () => admission.GetSnapshot().WaitingMetadataOperations == 1);
        await sweep.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, Volatile.Read(ref keepAliveCalls));
        var snapshot = admission.GetSnapshot();
        Assert.Equal(0, snapshot.ActiveMetadataOperations);
        Assert.Equal(0, snapshot.WaitingMetadataOperations);
    }

    [Fact]
    public async Task KeepAliveSkipsWhenHighPriorityBorrowerConsumesRestoredIdleConnection()
    {
        var created = 0;
        var keepAliveCalls = 0;
        var admissionEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<TestConnection>(
            maxConnections: 1,
            connectionFactory: _ => ValueTask.FromResult(
                new TestConnection(Interlocked.Increment(ref created))),
            idleTimeout: TimeSpan.FromMinutes(1),
            priorityOdds: new SemaphorePriorityOdds { HighPriorityOdds = 100 },
            warmConnectionFloor: 1,
            keepAlive: (_, _) =>
            {
                Interlocked.Increment(ref keepAliveCalls);
                return Task.CompletedTask;
            },
            keepAliveAdmission: async ct =>
            {
                admissionEntered.TrySetResult();
                await releaseAdmission.Task.WaitAsync(ct);
                return null;
            },
            keepAliveBorrowTimeout: TimeSpan.FromMilliseconds(200));

        await WaitUntilAsync(() => pool.LiveConnections == 1 && pool.IdleConnections == 1);
        var sweep = pool.SweepOnceForTestsAsync();
        await admissionEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var borrower = await pool.GetConnectionLockAsync(
            SemaphorePriority.High).WaitAsync(TimeSpan.FromSeconds(1));
        var queuedHighPriorityBorrower = pool.GetConnectionLockAsync(SemaphorePriority.High);
        Assert.False(queuedHighPriorityBorrower.IsCompleted);
        releaseAdmission.TrySetResult();

        await sweep.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, Volatile.Read(ref keepAliveCalls));
        Assert.Equal(1, created);
        Assert.Equal(1, pool.ActiveConnections);

        borrower.Dispose();
        using var nextBorrower = await queuedHighPriorityBorrower.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, created);
        nextBorrower.Dispose();

        await pool.SweepOnceForTestsAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref keepAliveCalls));
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

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var current = Volatile.Read(ref maximum);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref maximum, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private sealed class TestConnection(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool FailKeepAlive { get; init; }
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
