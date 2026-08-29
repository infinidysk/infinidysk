using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ConnectionPoolReplacementTests
{
    [Fact]
    public async Task RepeatedReplacement_DisposesBeforeReconnectWithoutExceedingLimit()
    {
        var livePhysicalConnections = 0;
        var maxPhysicalConnections = 0;
        var disposed = 0;
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: 1,
            _ =>
            {
                var live = Interlocked.Increment(ref livePhysicalConnections);
                UpdateMaximum(ref maxPhysicalConnections, live);
                return ValueTask.FromResult(new DisposableProbe(() =>
                {
                    Interlocked.Decrement(ref livePhysicalConnections);
                    Interlocked.Increment(ref disposed);
                }));
            },
            replacementHandshakeSpacing: TimeSpan.FromMilliseconds(5));

        for (var i = 0; i < 12; i++)
        {
            using var connection = await pool.GetConnectionLockAsync(SemaphorePriority.High);
            connection.Replace("read-timeout-BODY");
        }

        Assert.Equal(12, disposed);
        Assert.Equal(0, livePhysicalConnections);
        Assert.Equal(0, pool.LiveConnections);
        Assert.Equal(1, maxPhysicalConnections);
        Assert.Equal(12, pool.GetChurn().ConnectionsDestroyed);
    }

    [Fact]
    public async Task ConcurrentReplacements_PaceNewHandshakes()
    {
        var clock = new SignalingTimeProvider();
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: 2,
            _ => ValueTask.FromResult(new DisposableProbe(() => { })),
            replacementHandshakeSpacing: TimeSpan.FromSeconds(1),
            timeProvider: clock);

        var first = await pool.GetConnectionLockAsync(SemaphorePriority.High);
        var second = await pool.GetConnectionLockAsync(SemaphorePriority.High);
        first.Replace("read-timeout-BODY");
        second.Replace("read-timeout-ARTICLE");
        first.Dispose();
        second.Dispose();

        var firstTimer = clock.WaitForNextTimerAsync();
        var secondTimer = clock.WaitForNextTimerAsync();
        var replacements = new[]
        {
            pool.GetConnectionLockAsync(SemaphorePriority.High),
            pool.GetConnectionLockAsync(SemaphorePriority.High),
        };
        await Task.WhenAll(firstTimer, secondTimer).WaitAsync(TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(1));
        var firstCompleted = await Task.WhenAny(replacements).WaitAsync(TimeSpan.FromSeconds(1));
        await firstCompleted.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, replacements.Count(task => task.IsCompleted));

        clock.Advance(TimeSpan.FromSeconds(1));
        var acquired = await Task.WhenAll(replacements).WaitAsync(TimeSpan.FromSeconds(1));
        foreach (var replacement in acquired) replacement.Dispose();

        Assert.Equal(2, pool.LiveConnections);
        Assert.Equal(2, pool.IdleConnections);
    }

    [Fact]
    public async Task ReplacementReservations_ExtendPacingWindowForLargeQueue()
    {
        const int poolWidth = 15;
        var clock = new SignalingTimeProvider();
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: poolWidth,
            _ => ValueTask.FromResult(new DisposableProbe(() => { })),
            replacementHandshakeSpacing: TimeSpan.FromSeconds(1),
            timeProvider: clock);

        var originals = await Task.WhenAll(Enumerable.Range(0, poolWidth)
            .Select(_ => pool.GetConnectionLockAsync(SemaphorePriority.High)));
        foreach (var original in originals)
        {
            original.Replace("read-timeout-BODY");
            original.Dispose();
        }

        var initialTimers = Enumerable.Range(0, 3)
            .Select(_ => clock.WaitForNextTimerAsync())
            .ToArray();
        var replacements = Enumerable.Range(0, poolWidth)
            .Select(_ => pool.GetConnectionLockAsync(SemaphorePriority.High))
            .ToArray();
        await Task.WhenAll(initialTimers).WaitAsync(TimeSpan.FromSeconds(1));

        // Each completed delay admits one queued borrower through the three-slot
        // handshake gate. The tenth admission crosses the original fixed window.
        for (var i = 0; i < poolWidth - 3; i++)
        {
            var nextTimer = clock.WaitForNextTimerAsync();
            clock.Advance(TimeSpan.FromSeconds(1));
            await nextTimer.WaitAsync(TimeSpan.FromSeconds(1));
        }

        var allReplacements = Task.WhenAll(replacements);
        for (var i = 0; i < 4 && !allReplacements.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
        var acquired = await allReplacements.WaitAsync(TimeSpan.FromSeconds(1));
        foreach (var replacement in acquired) replacement.Dispose();

        Assert.Equal(poolWidth, pool.LiveConnections);
        Assert.Equal(poolWidth, pool.IdleConnections);
    }

    [Fact]
    public async Task RepeatedHandshakeFailures_BackOffAndReleasePoolPermit()
    {
        var clock = new SignalingTimeProvider();
        var attempt = 0;
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: 1,
            _ =>
            {
                if (Interlocked.Increment(ref attempt) <= 3)
                    throw new IOException("AUTHINFO failed");
                return ValueTask.FromResult(new DisposableProbe(() => { }));
            },
            replacementHandshakeSpacing: TimeSpan.FromSeconds(1),
            timeProvider: clock);

        await Assert.ThrowsAsync<IOException>(async () =>
            await pool.GetConnectionLockAsync(SemaphorePriority.High));
        Assert.Equal(1, pool.AvailableConnections);

        for (var i = 0; i < 2; i++)
        {
            var retryTimer = clock.WaitForNextTimerAsync();
            var retry = Assert.ThrowsAsync<IOException>(async () =>
                await pool.GetConnectionLockAsync(SemaphorePriority.High));
            await retryTimer.WaitAsync(TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1 << i));
            await retry;
            Assert.Equal(1, pool.AvailableConnections);
            Assert.Equal(0, pool.LiveConnections);
        }

        var successTimer = clock.WaitForNextTimerAsync();
        var success = pool.GetConnectionLockAsync(SemaphorePriority.High);
        await successTimer.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(4));
        using (await success.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(1, pool.LiveConnections);
        }

        Assert.Equal(3, pool.GetChurn().HandshakeFailures);
    }

    [Fact]
    public async Task ZeroReplacementSpacing_StillBacksOffFactoryFailures()
    {
        var clock = new SignalingTimeProvider();
        var attempt = 0;
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: 1,
            _ =>
            {
                if (Interlocked.Increment(ref attempt) <= 2)
                    throw new IOException("AUTHINFO failed");
                return ValueTask.FromResult(new DisposableProbe(() => { }));
            },
            replacementHandshakeSpacing: TimeSpan.Zero,
            timeProvider: clock);

        await Assert.ThrowsAsync<IOException>(async () =>
            await pool.GetConnectionLockAsync(SemaphorePriority.High));

        var retryTimer = clock.WaitForNextTimerAsync();
        var retry = Assert.ThrowsAsync<IOException>(async () =>
            await pool.GetConnectionLockAsync(SemaphorePriority.High));
        await retryTimer.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMilliseconds(ConnectionPool<DisposableProbe>.MinimumHandshakeFailureBackoffMs));
        await retry;

        var successTimer = clock.WaitForNextTimerAsync();
        var success = pool.GetConnectionLockAsync(SemaphorePriority.High);
        await successTimer.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMilliseconds(
            ConnectionPool<DisposableProbe>.MinimumHandshakeFailureBackoffMs * 2));
        using (await success.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(1, pool.LiveConnections);
        }

        Assert.Equal(2, pool.GetChurn().HandshakeFailures);
    }

    [Fact]
    public async Task SuccessfulHandshake_ResetsFailureBackoff()
    {
        var clock = new SignalingTimeProvider();
        var attempt = 0;
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: 1,
            _ =>
            {
                var n = Interlocked.Increment(ref attempt);
                if (n is 1 or 3)
                    throw new IOException("AUTHINFO failed");
                return ValueTask.FromResult(new DisposableProbe(() => { }));
            },
            replacementHandshakeSpacing: TimeSpan.Zero,
            timeProvider: clock);

        await Assert.ThrowsAsync<IOException>(async () =>
            await pool.GetConnectionLockAsync(SemaphorePriority.High)
                .WaitAsync(TimeSpan.FromSeconds(1)));

        var firstSuccessTimer = clock.WaitForNextTimerAsync();
        var firstSuccess = pool.GetConnectionLockAsync(SemaphorePriority.High);
        await firstSuccessTimer.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMilliseconds(ConnectionPool<DisposableProbe>.MinimumHandshakeFailureBackoffMs));
        var first = await firstSuccess.WaitAsync(TimeSpan.FromSeconds(1));
        first.Replace("read-timeout-BODY");
        first.Dispose();

        var secondFailTimer = clock.WaitForNextTimerSource();
        var secondFail = pool.GetConnectionLockAsync(SemaphorePriority.High);
        var secondFailStarted = await Task.WhenAny(secondFailTimer.Task, secondFail)
            .WaitAsync(TimeSpan.FromSeconds(1));
        if (ReferenceEquals(secondFailStarted, secondFailTimer.Task))
            clock.Advance(TimeSpan.FromMilliseconds(ConnectionPool<DisposableProbe>.MinimumHandshakeFailureBackoffMs));
        else
            secondFailTimer.TrySetCanceled();
        await Assert.ThrowsAsync<IOException>(async () =>
            await secondFail.WaitAsync(TimeSpan.FromSeconds(1)));

        var secondSuccessTimer = clock.WaitForNextTimerAsync();
        var secondSuccess = pool.GetConnectionLockAsync(SemaphorePriority.High);
        await secondSuccessTimer.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMilliseconds(ConnectionPool<DisposableProbe>.MinimumHandshakeFailureBackoffMs));
        using (await secondSuccess.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(1, pool.LiveConnections);
        }
    }

    [Fact]
    public async Task ConcurrentHandshakeFailures_PreserveLongestBackoffDeadline()
    {
        var clock = new SignalingTimeProvider();
        var attempts = 0;
        var releaseFailures = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: 3,
            async _ =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt <= 3)
                {
                    if (attempt == 3) releaseFailures.TrySetResult();
                    await releaseFailures.Task;
                    throw new IOException("AUTHINFO failed");
                }

                return new DisposableProbe(() => { });
            },
            replacementHandshakeSpacing: TimeSpan.FromSeconds(1),
            timeProvider: clock);

        var failures = Enumerable.Range(0, 3)
            .Select(_ => Assert.ThrowsAsync<IOException>(async () =>
                await pool.GetConnectionLockAsync(SemaphorePriority.High)))
            .ToArray();
        await Task.WhenAll(failures);

        var retryTimer = clock.WaitForNextTimerAsync();
        var retry = pool.GetConnectionLockAsync(SemaphorePriority.High);
        await retryTimer.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(retry.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        using (await retry.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(1, pool.LiveConnections);
        }

        Assert.Equal(3, pool.GetChurn().HandshakeFailures);
    }

    [Fact]
    public async Task CancelledReplacementFactory_RetainsSpacingWithoutCountingFailure()
    {
        var clock = new SignalingTimeProvider();
        var factoryCalls = 0;
        var cancelledFactoryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledFactoryDisposed = 0;
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: 1,
            async cancellationToken =>
            {
                var call = Interlocked.Increment(ref factoryCalls);
                if (call == 2)
                {
                    var probe = new DisposableProbe(() => Interlocked.Increment(ref cancelledFactoryDisposed));
                    cancelledFactoryStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        probe.Dispose();
                        throw;
                    }
                }

                return new DisposableProbe(() => { });
            },
            replacementHandshakeSpacing: TimeSpan.FromSeconds(1),
            timeProvider: clock);

        var first = await pool.GetConnectionLockAsync(SemaphorePriority.High);
        first.Replace("read-timeout-BODY");
        first.Dispose();

        clock.Advance(TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        var cancelledBorrow = pool.GetConnectionLockAsync(
            SemaphorePriority.High, cancellation.Token);
        await cancelledFactoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelledBorrow.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(0, pool.GetChurn().HandshakeFailures);
        Assert.Equal(1, pool.AvailableConnections);
        Assert.Equal(1, cancelledFactoryDisposed);

        var laterDelay = clock.WaitForNextTimerAsync();
        var laterBorrow = pool.GetConnectionLockAsync(SemaphorePriority.High);
        await laterDelay.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        using (await laterBorrow.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(1, pool.LiveConnections);
        }
    }

    [Fact]
    public async Task CancelledReplacementPacingWait_DoesNotDelayNextBorrower()
    {
        var clock = new SignalingTimeProvider();
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: 1,
            _ => ValueTask.FromResult(new DisposableProbe(() => { })),
            replacementHandshakeSpacing: TimeSpan.FromSeconds(1),
            timeProvider: clock);

        var first = await pool.GetConnectionLockAsync(SemaphorePriority.High);
        first.Replace("read-timeout-ARTICLE");
        first.Dispose();

        using var cancellation = new CancellationTokenSource();
        var cancelledDelayStarted = clock.WaitForNextTimerAsync();
        var cancelledBorrow = pool.GetConnectionLockAsync(
            SemaphorePriority.High, cancellation.Token);
        await cancelledDelayStarted.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelledBorrow.WaitAsync(TimeSpan.FromSeconds(1)));

        var laterDelayStarted = clock.WaitForNextTimerAsync();
        var laterBorrow = pool.GetConnectionLockAsync(SemaphorePriority.High);
        await laterDelayStarted.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        using (await laterBorrow.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(1, pool.LiveConnections);
        }

        Assert.Equal(0, pool.GetChurn().HandshakeFailures);
    }

    [Fact]
    public async Task CancelledPacingWaits_CollapseNonTailReservations()
    {
        var clock = new SignalingTimeProvider();
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: 2,
            _ => ValueTask.FromResult(new DisposableProbe(() => { })),
            replacementHandshakeSpacing: TimeSpan.FromSeconds(1),
            timeProvider: clock);

        var originals = await Task.WhenAll(
            pool.GetConnectionLockAsync(SemaphorePriority.High),
            pool.GetConnectionLockAsync(SemaphorePriority.High));
        foreach (var original in originals)
        {
            original.Replace("read-timeout-BODY");
            original.Dispose();
        }

        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var firstDelayStarted = clock.WaitForNextTimerAsync();
        var secondDelayStarted = clock.WaitForNextTimerAsync();
        var firstBorrow = pool.GetConnectionLockAsync(
            SemaphorePriority.High, firstCancellation.Token);
        var secondBorrow = pool.GetConnectionLockAsync(
            SemaphorePriority.High, secondCancellation.Token);
        await Task.WhenAll(firstDelayStarted, secondDelayStarted)
            .WaitAsync(TimeSpan.FromSeconds(1));

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await firstBorrow.WaitAsync(TimeSpan.FromSeconds(1)));
        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await secondBorrow.WaitAsync(TimeSpan.FromSeconds(1)));

        var laterDelayStarted = clock.WaitForNextTimerAsync();
        var laterBorrow = pool.GetConnectionLockAsync(SemaphorePriority.High);
        await laterDelayStarted.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        using (await laterBorrow.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(1, pool.LiveConnections);
        }

        Assert.Equal(0, pool.GetChurn().HandshakeFailures);
    }

    [Fact]
    public async Task CancelledFactory_WithZeroSpacing_DoesNotArmFailureBackoff()
    {
        var clock = new SignalingTimeProvider();
        var cancelledFactoryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: 1,
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref factoryCalls) == 1)
                {
                    cancelledFactoryStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return new DisposableProbe(() => { });
            },
            replacementHandshakeSpacing: TimeSpan.Zero,
            timeProvider: clock);

        using var cancellation = new CancellationTokenSource();
        var cancelledBorrow = pool.GetConnectionLockAsync(
            SemaphorePriority.High, cancellation.Token);
        await cancelledFactoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelledBorrow.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(0, pool.GetChurn().HandshakeFailures);
        Assert.Equal(1, pool.AvailableConnections);

        var laterBorrow = pool.GetConnectionLockAsync(SemaphorePriority.High);
        using (await laterBorrow.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(1, pool.LiveConnections);
        }
    }

    [Fact]
    public async Task PacingCancellationThenFactoryCancellation_FactoryStartKeepsSpacing()
    {
        var clock = new SignalingTimeProvider();
        var factoryCalls = 0;
        var blockedFactoryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var pool = new ConnectionPool<DisposableProbe>(
            maxConnections: 2,
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref factoryCalls) == 3)
                {
                    blockedFactoryStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return new DisposableProbe(() => { });
            },
            replacementHandshakeSpacing: TimeSpan.FromSeconds(1),
            timeProvider: clock);

        var originals = await Task.WhenAll(
            pool.GetConnectionLockAsync(SemaphorePriority.High),
            pool.GetConnectionLockAsync(SemaphorePriority.High));
        foreach (var original in originals)
        {
            original.Replace("read-timeout-ARTICLE");
            original.Dispose();
        }

        using var pacingCancellation = new CancellationTokenSource();
        using var factoryCancellation = new CancellationTokenSource();
        var firstDelayStarted = clock.WaitForNextTimerAsync();
        var secondDelayStarted = clock.WaitForNextTimerAsync();
        var pacingBorrow = pool.GetConnectionLockAsync(
            SemaphorePriority.High, pacingCancellation.Token);
        var factoryBorrow = pool.GetConnectionLockAsync(
            SemaphorePriority.High, factoryCancellation.Token);
        await Task.WhenAll(firstDelayStarted, secondDelayStarted)
            .WaitAsync(TimeSpan.FromSeconds(1));

        pacingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pacingBorrow.WaitAsync(TimeSpan.FromSeconds(1)));
        clock.Advance(TimeSpan.FromSeconds(2));
        await blockedFactoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        factoryCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await factoryBorrow.WaitAsync(TimeSpan.FromSeconds(1)));

        var laterDelayStarted = clock.WaitForNextTimerAsync();
        var laterBorrow = pool.GetConnectionLockAsync(SemaphorePriority.High);
        await laterDelayStarted.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        using (await laterBorrow.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(1, pool.LiveConnections);
        }

        Assert.Equal(0, pool.GetChurn().HandshakeFailures);
    }

    private sealed class SignalingTimeProvider : TimeProvider
    {
        private readonly ControllableTimeProvider _inner = new();
        private readonly ConcurrentQueue<TaskCompletionSource> _timerWaiters = new();

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            while (_timerWaiters.TryDequeue(out var waiter))
            {
                if (waiter.TrySetResult())
                    break;
            }
            return timer;
        }

        public Task WaitForNextTimerAsync() => WaitForNextTimerSource().Task;

        public TaskCompletionSource WaitForNextTimerSource()
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _timerWaiters.Enqueue(waiter);
            return waiter;
        }

        public void Advance(TimeSpan delta) => _inner.Advance(delta);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var current = Volatile.Read(ref maximum);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref maximum, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private sealed class DisposableProbe(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
        }
    }
}
