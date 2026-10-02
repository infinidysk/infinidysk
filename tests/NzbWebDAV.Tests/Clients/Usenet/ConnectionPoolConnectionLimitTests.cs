using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Logging;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalLoggerCollection))]
public class ConnectionPoolConnectionLimitTests : IDisposable
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

    public ConnectionPoolConnectionLimitTests()
        => SynchronousObserverInvoker.ResetFailureLogThrottleForTests();

    public void Dispose()
        => SynchronousObserverInvoker.ResetFailureLogThrottleForTests();

    private static ConnectionPool<object> CreatePool(
        int maxConnections,
        Func<Exception, int?>? detector = null,
        Action<int, int>? onLearned = null,
        Func<CancellationToken, ValueTask<object>>? factory = null) =>
        new(
            maxConnections,
            factory ?? (_ => ValueTask.FromResult(new object())),
            TimeSpan.FromMinutes(5),
            priorityOdds: null,
            connectionLimitDetector: detector,
            onConnectionLimitLearned: onLearned);

    private static Func<Exception, int?> Detector502(int learned) =>
        ex => ex is CouldNotLoginToUsenetException { ResponseCode: 502 } ? learned : null;

    [Fact]
    public async Task ConnectionLimit502_ShrinksEffectiveMax()
    {
        var learnedValues = new List<(int learned, int effective)>();
        await using var pool = CreatePool(
            maxConnections: 150,
            detector: Detector502(150),
            onLearned: (learned, effective) => learnedValues.Add((learned, effective)),
            factory: _ => throw new CouldNotLoginToUsenetException(
                "Could not login to usenet host: 502 connection limit (150) reached",
                responseCode: 502));

        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None).WaitAsync(WaitBudget));

        Assert.Equal(135, pool.EffectiveMaxConnections);
        Assert.Equal(150, pool.LearnedConnectionLimit);
        Assert.Single(learnedValues);
        Assert.Equal((150, 135), learnedValues[0]);
    }

    [Fact]
    public async Task RepeatedSameLimit_DoesNotShrinkAgain()
    {
        var callbackCount = 0;
        await using var pool = CreatePool(
            maxConnections: 150,
            detector: Detector502(150),
            onLearned: (_, _) => Interlocked.Increment(ref callbackCount),
            factory: _ => throw new CouldNotLoginToUsenetException(
                "Could not login to usenet host: 502 connection limit (150) reached",
                responseCode: 502));

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
                () => pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None).WaitAsync(WaitBudget));
        }

        Assert.Equal(135, pool.EffectiveMaxConnections);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task LowerSecondLimit_ShrinksFurther()
    {
        var learnedValues = new List<(int learned, int effective)>();
        var learned = 150;
        await using var pool = CreatePool(
            maxConnections: 150,
            detector: _ => learned,
            onLearned: (l, e) => learnedValues.Add((l, e)),
            factory: _ => throw new CouldNotLoginToUsenetException(
                "502 connection limit reached", responseCode: 502));

        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None).WaitAsync(WaitBudget));
        Assert.Equal(135, pool.EffectiveMaxConnections);

        learned = 100;
        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None).WaitAsync(WaitBudget));
        Assert.Equal(90, pool.EffectiveMaxConnections);

        Assert.Equal(2, learnedValues.Count);
        Assert.Equal((150, 135), learnedValues[0]);
        Assert.Equal((100, 90), learnedValues[1]);
    }

    [Fact]
    public async Task LearnedTwo_HardFloorAtOne()
    {
        await using var pool = CreatePool(
            maxConnections: 10,
            detector: Detector502(2),
            factory: _ => throw new CouldNotLoginToUsenetException(
                "502 connection limit (2) reached", responseCode: 502));

        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None).WaitAsync(WaitBudget));

        Assert.Equal(1, pool.EffectiveMaxConnections);
    }

    [Fact]
    public async Task Non502_DoesNotShrink()
    {
        await using var pool = CreatePool(
            maxConnections: 10,
            detector: _ => null, // detector returns null for non-502
            factory: _ => throw new CouldNotLoginToUsenetException(
                "Could not login to usenet host: 481 authentication rejected"));

        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None).WaitAsync(WaitBudget));

        Assert.Equal(10, pool.EffectiveMaxConnections);
        Assert.Null(pool.LearnedConnectionLimit);
    }

    [Fact]
    public async Task NoDetector_NoShrink()
    {
        await using var pool = CreatePool(
            maxConnections: 10,
            factory: _ => throw new CouldNotLoginToUsenetException(
                "502 connection limit (5) reached", responseCode: 502));

        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None).WaitAsync(WaitBudget));

        Assert.Equal(10, pool.EffectiveMaxConnections);
    }

    [Fact]
    public async Task ConcurrentFailures_OnlyOneCallback()
    {
        var callbackCount = 0;
        var barrier = new TaskCompletionSource();
        await using var pool = CreatePool(
            maxConnections: 10,
            detector: Detector502(10),
            onLearned: (_, _) => Interlocked.Increment(ref callbackCount),
            factory: async _ =>
            {
                // Both factory calls wait at the barrier, then both throw.
                await barrier.Task;
                throw new CouldNotLoginToUsenetException(
                    "502 connection limit (10) reached", responseCode: 502);
            });

        var t1 = pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None);
        var t2 = pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None);
        barrier.SetResult();

        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(() => t1.WaitAsync(WaitBudget));
        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(() => t2.WaitAsync(WaitBudget));

        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task AvailableConnections_NeverNegative()
    {
        await using var pool = CreatePool(
            maxConnections: 5,
            detector: Detector502(3),
            factory: _ => throw new CouldNotLoginToUsenetException(
                "502 connection limit (3) reached", responseCode: 502));

        // learned=3, headroom=max(2,0)=2, candidate=max(1,1)=1 → effective shrinks to 1.
        // With 0 active connections, AvailableConnections should be 1 (not negative).
        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None).WaitAsync(WaitBudget));

        Assert.True(pool.AvailableConnections >= 0);
    }

    [Fact]
    public async Task GateCapsAtEffectiveMax()
    {
        var factoryCallCount = 0;
        var shouldFail = false;
        await using var pool = CreatePool(
            maxConnections: 5,
            detector: Detector502(5),
            factory: _ =>
            {
                if (shouldFail)
                    throw new CouldNotLoginToUsenetException(
                        "502 connection limit (5) reached", responseCode: 502);
                Interlocked.Increment(ref factoryCallCount);
                return ValueTask.FromResult(new object());
            });

        // Acquire 5 connections successfully.
        var locks = new List<ConnectionLock<object>>();
        for (var i = 0; i < 5; i++)
            locks.Add(await pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None).WaitAsync(WaitBudget));
        Assert.Equal(5, pool.ActiveConnections);

        // Now shrink: destroy one connection so the next acquire calls the factory,
        // which fails with 502 limit(5) → effective = 5-2=3.
        locks[0].Replace();
        locks[0].Dispose();
        shouldFail = true;
        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None).WaitAsync(WaitBudget));
        Assert.Equal(3, pool.EffectiveMaxConnections);

        // We hold 4 locks (5 - 1 returned). Active = 4. Effective = 3.
        // The gate should block new acquisitions since active >= effective.
        // Return all and verify we can only acquire 3.
        foreach (var l in locks.Skip(1)) l.Dispose();
        locks.Clear();
        shouldFail = false;

        for (var i = 0; i < 3; i++)
            locks.Add(await pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None).WaitAsync(WaitBudget));
        Assert.Equal(3, pool.ActiveConnections);

        // The 4th acquisition should block (gate at 3).
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High, cts.Token).WaitAsync(WaitBudget));

        foreach (var l in locks) l.Dispose();
    }

    [Fact]
    public async Task ConnectionLimitObservers_ThrowingFirstSubscribers_PreserveFactoryErrorAndGate()
    {
        var factoryError = new CouldNotLoginToUsenetException(
            "Could not login to usenet host: 502 connection limit (2) reached",
            responseCode: 502);
        var factoryCalls = 0;
        var poolOrder = new List<string>();
        var learnedOrder = new List<string>();
        var learnedValues = new List<(int learned, int effective)>();
        var throwStats = true;
        Action<int, int> onLearned = (_, _) =>
        {
            learnedOrder.Add("first");
            throw new InvalidOperationException("learned observer");
        };
        onLearned += (learned, effective) =>
        {
            learnedOrder.Add("second");
            learnedValues.Add((learned, effective));
        };

        await using var pool = CreatePool(
            maxConnections: 2,
            detector: Detector502(2),
            onLearned: onLearned,
            factory: _ =>
            {
                if (Interlocked.Increment(ref factoryCalls) == 1)
                    throw factoryError;
                return ValueTask.FromResult(new object());
            });

        pool.OnConnectionPoolChanged += (_, _) =>
        {
            poolOrder.Add("first");
            if (throwStats)
                throw new InvalidOperationException("stats observer");
        };
        pool.OnConnectionPoolChanged += (_, _) => poolOrder.Add("second");

        var thrown = await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None)
                .WaitAsync(WaitBudget));
        Assert.Same(factoryError, thrown);
        Assert.Equal(1, pool.EffectiveMaxConnections);
        Assert.Equal(2, pool.LearnedConnectionLimit);
        Assert.Equal(["first", "second"], poolOrder);
        Assert.Equal(["first", "second"], learnedOrder);
        Assert.Equal([(2, 1)], learnedValues);

        throwStats = false;
        var recovered = await pool.GetConnectionLockAsync(SemaphorePriority.High, CancellationToken.None)
            .WaitAsync(WaitBudget);
        recovered.Dispose();
    }

    private static CouldNotLoginToUsenetException PlainLimitRejection() =>
        new("Could not login to usenet host: 502 Too many connections.", responseCode: 502);

    private static ConnectionPool<object> CreateRejectionPool(
        int maxConnections,
        ControllableTimeProvider clock,
        Func<CancellationToken, ValueTask<object>> factory,
        Func<Exception, int?>? detector = null) =>
        new(
            maxConnections,
            factory,
            TimeSpan.FromMinutes(5),
            connectionLimitDetector: detector,
            timeProvider: clock,
            connectionLimitRejectionDetector: UsenetConnectionLimitDetector.IsConnectionLimitRejection);

    [Fact]
    public async Task PlainLimitRejection_CapsAtLiveConnectionsThenWidensOneStepAtATime()
    {
        var clock = new ControllableTimeProvider();
        var reject = false;
        await using var pool = CreateRejectionPool(6, clock, _ => reject
            ? throw PlainLimitRejection()
            : ValueTask.FromResult(new object()));
        var locks = new List<ConnectionLock<object>>();
        for (var i = 0; i < 3; i++)
            locks.Add(await pool.GetConnectionLockAsync(SemaphorePriority.High).WaitAsync(WaitBudget));

        reject = true;
        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High).WaitAsync(WaitBudget));

        Assert.Equal(3, pool.EffectiveMaxConnections);
        Assert.Null(pool.LearnedConnectionLimit);
        clock.Advance(ConnectionPool<object>.ConnectionLimitRecoveryStep - TimeSpan.FromMilliseconds(1));
        Assert.Equal(3, pool.EffectiveMaxConnections);
        int[] widened = [4, 5, 6, 6];
        foreach (var expected in widened)
        {
            clock.Advance(ConnectionPool<object>.ConnectionLimitRecoveryStep);
            Assert.Equal(expected, pool.EffectiveMaxConnections);
        }
        Assert.Equal(3, pool.LiveConnections);
        foreach (var held in locks) held.Dispose();
    }

    [Fact]
    public async Task RepeatedPlainLimitRejections_RetryAfterFixedDelay()
    {
        var clock = new ControllableTimeProvider();
        await using var pool = CreateRejectionPool(1, clock, _ => throw PlainLimitRejection());

        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High).WaitAsync(WaitBudget));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var retry = pool.GetConnectionLockAsync(SemaphorePriority.High);
            Assert.True(SpinWait.SpinUntil(() => clock.HasScheduledTimer, WaitBudget));
            // Exponential handshake backoff would still be waiting after a fixed step.
            clock.Advance(TimeSpan.FromMilliseconds(ConnectionPool<object>.ConnectionLimitRetryDelayMs));
            await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(() => retry.WaitAsync(WaitBudget));
        }
        Assert.Equal(6, pool.GetChurn().HandshakeFailures);
    }

    [Fact]
    public async Task PlainLimitRejections_DoNotInflateNextTransportFailureBackoff()
    {
        var clock = new ControllableTimeProvider();
        var calls = 0;
        await using var pool = CreateRejectionPool(1, clock, _ => Interlocked.Increment(ref calls) switch
        {
            <= 6 => throw PlainLimitRejection(),
            7 => throw new IOException("Connection reset by peer."),
            _ => ValueTask.FromResult(new object()),
        });

        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High).WaitAsync(WaitBudget));
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var retry = pool.GetConnectionLockAsync(SemaphorePriority.High);
            Assert.True(SpinWait.SpinUntil(() => clock.HasScheduledTimer, WaitBudget));
            clock.Advance(TimeSpan.FromMilliseconds(ConnectionPool<object>.ConnectionLimitRetryDelayMs));
            await Assert.ThrowsAnyAsync<Exception>(() => retry.WaitAsync(WaitBudget));
        }
        Assert.Equal(7, Volatile.Read(ref calls));

        // The transport failure is the first of its streak, so it waits the base delay, not 32 s.
        var recovered = pool.GetConnectionLockAsync(SemaphorePriority.High);
        Assert.True(SpinWait.SpinUntil(() => clock.HasScheduledTimer, WaitBudget));
        clock.Advance(TimeSpan.FromMilliseconds(ConnectionPool<object>.MinimumHandshakeFailureBackoffMs));
        using var connection = await recovered.WaitAsync(WaitBudget);
        Assert.Equal(7, pool.GetChurn().HandshakeFailures);
    }

    [Fact]
    public async Task ConnectionLimitRecovery_AdmitsQueuedTransferWithoutRelease()
    {
        var clock = new ControllableTimeProvider();
        var reject = false;
        await using var pool = CreateRejectionPool(4, clock, _ => reject
            ? throw PlainLimitRejection()
            : ValueTask.FromResult(new object()));
        using var admission = ProviderConnectionAdmission.ForPool(pool, configuredTransferLimit: 4);
        using var firstTransfer = await admission.AcquireAsync(
            ProviderConnectionKind.Transfer, SemaphorePriority.High, CancellationToken.None);
        using var firstConnection = await pool.GetConnectionLockAsync(SemaphorePriority.High)
            .WaitAsync(WaitBudget);

        reject = true;
        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High).WaitAsync(WaitBudget));
        Assert.Equal(1, pool.EffectiveMaxConnections);
        var secondTransfer = admission.AcquireAsync(
            ProviderConnectionKind.Transfer, SemaphorePriority.High, CancellationToken.None);
        Assert.False(secondTransfer.IsCompleted);

        reject = false;
        clock.Advance(ConnectionPool<object>.ConnectionLimitRecoveryStep);

        using var secondLease = await secondTransfer.WaitAsync(WaitBudget);
        using var secondConnection = await pool.GetConnectionLockAsync(SemaphorePriority.High)
            .WaitAsync(WaitBudget);
        Assert.Equal(2, pool.LiveConnections);
    }

    [Fact]
    public async Task PlainLimitRejection_RecoveryStopsAtAdvertisedLimitCeiling()
    {
        var clock = new ControllableTimeProvider();
        await using var pool = CreateRejectionPool(
            20, clock,
            _ => throw new CouldNotLoginToUsenetException(
                "502 connection limit (10) reached", responseCode: 502),
            detector: ex => UsenetConnectionLimitDetector.TryLearn(ex, out var learned) ? learned : null);

        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.High).WaitAsync(WaitBudget));
        Assert.Equal(1, pool.EffectiveMaxConnections);

        for (var step = 0; step < 20; step++)
            clock.Advance(ConnectionPool<object>.ConnectionLimitRecoveryStep);
        Assert.Equal(8, pool.EffectiveMaxConnections);
        Assert.Equal(10, pool.LearnedConnectionLimit);
    }
}
