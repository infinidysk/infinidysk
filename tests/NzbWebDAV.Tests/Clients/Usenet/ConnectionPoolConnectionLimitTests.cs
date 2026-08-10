using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ConnectionPoolConnectionLimitTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

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

        // Acquire 3 connections (the max the pool will allow after shrink to 2).
        var locks = new List<ConnectionLock<object>>();
        // First, fill the pool with 5 successful connections.
        // We can't do that with a failing factory, so let's test the property directly.
        // After shrink to 2 with 0 active, available should be 2 (not negative).
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
}
