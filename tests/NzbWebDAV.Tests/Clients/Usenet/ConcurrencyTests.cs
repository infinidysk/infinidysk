using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using UsenetSharp.Concurrency;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ConcurrencyTests
{
    [Fact]
    public void ProviderCircuitBreaker_TripsAfterThreeFailuresAndResetsOnSuccess()
    {
        var breaker = new ProviderCircuitBreaker("test");

        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.False(breaker.IsTripped);

        breaker.RecordFailure();
        Assert.True(breaker.IsTripped);

        breaker.ExpireCooldownForTests();
        breaker.RecordSuccess();
        Assert.False(breaker.IsTripped);
        Assert.Equal(0, breaker.TrippedUntilMs);
        Assert.Equal(TimeSpan.FromSeconds(60), breaker.CurrentCooldown);
    }

    [Fact]
    public void ProviderCircuitBreaker_EmitsOneOpenAndClosedTransition()
    {
        var transitions = new List<ProviderCircuitTransition>();
        var breaker = new ProviderCircuitBreaker("events", transitions.Add);

        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.Empty(transitions);
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.Single(transitions);
        breaker.ExpireCooldownForTests();
        breaker.RecordSuccess();
        breaker.RecordSuccess();

        Assert.Collection(
            transitions,
            opened =>
            {
                Assert.Equal(ProviderCircuitTransitionState.Open, opened.State);
                Assert.Equal(TimeSpan.FromSeconds(60), opened.Cooldown);
                Assert.True(opened.AtUnixMilliseconds > 0);
            },
            closed =>
            {
                Assert.Equal(ProviderCircuitTransitionState.Closed, closed.State);
                Assert.Null(closed.Cooldown);
                Assert.True(closed.AtUnixMilliseconds >= transitions[0].AtUnixMilliseconds);
            });
    }

    [Fact]
    public void ProviderCircuitBreaker_DoesNotEmitClosedForTransientFailures()
    {
        var transitions = new List<ProviderCircuitTransition>();
        var breaker = new ProviderCircuitBreaker("events", transitions.Add);

        breaker.RecordFailure();
        breaker.RecordSuccess();

        Assert.Empty(transitions);
    }

    [Fact]
    public void ProviderCircuitBreaker_ContainsTransitionCallbackFailures()
    {
        var breaker = new ProviderCircuitBreaker(
            "events",
            _ => throw new InvalidOperationException("metrics unavailable"));

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();

        Assert.True(breaker.IsTripped);
    }

    [Fact]
    public void ProviderCircuitBreaker_BurstOfFailuresProducesExactlyOneTrip()
    {
        var breaker = new ProviderCircuitBreaker("burst");

        for (var i = 0; i < 20; i++)
            breaker.RecordFailure();

        Assert.True(breaker.IsTripped);
        // First trip uses the initial 60s cooldown; ladder advances once for the next trip.
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);
    }

    [Fact]
    public void ProviderCircuitBreaker_FailuresWhileTrippedDoNotExtendWindow()
    {
        var breaker = new ProviderCircuitBreaker("latched");

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.True(breaker.IsTripped);

        var trippedUntil = breaker.TrippedUntilMs;
        Assert.True(trippedUntil > 0);

        for (var i = 0; i < 10; i++)
            breaker.RecordFailure();

        Assert.Equal(trippedUntil, breaker.TrippedUntilMs);
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);
    }

    [Fact]
    public void ProviderCircuitBreaker_SuccessResetsCooldownLadder()
    {
        var breaker = new ProviderCircuitBreaker("ladder");

        // Trip once so the next-trip cooldown doubles to 120s.
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);

        breaker.ExpireCooldownForTests();
        breaker.RecordSuccess();
        Assert.Equal(TimeSpan.FromSeconds(60), breaker.CurrentCooldown);

        // After reset, need a full threshold of fresh failures to trip again.
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.False(breaker.IsTripped);
        breaker.RecordFailure();
        Assert.True(breaker.IsTripped);
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);
    }

    [Fact]
    public void ProviderCircuitBreaker_HalfOpenAdmitsExactlyOneProbe()
    {
        var breaker = new ProviderCircuitBreaker("half-open");
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.True(breaker.IsTripped);

        breaker.ExpireCooldownForTests();

        Assert.False(breaker.IsTripped); // first caller wins the probe
        Assert.True(breaker.IsTripped);  // everyone else stays blocked
        Assert.True(breaker.IsTripped);
    }

    [Fact]
    public void ProviderCircuitBreaker_ProbeFailureReTripsWithDoubledCooldown()
    {
        var breaker = new ProviderCircuitBreaker("probe-fail");
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);

        breaker.ExpireCooldownForTests();
        Assert.False(breaker.IsTripped); // take probe

        breaker.RecordFailure();
        Assert.True(breaker.IsTripped);
        Assert.True(breaker.TrippedUntilMs > Environment.TickCount64);
        // Re-trip used the 120s cooldown, then ladder advanced to 240s.
        Assert.Equal(TimeSpan.FromSeconds(240), breaker.CurrentCooldown);
    }

    [Fact]
    public void ProviderCircuitBreaker_ProbeSuccessFullyCloses()
    {
        var breaker = new ProviderCircuitBreaker("probe-ok");
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);

        breaker.ExpireCooldownForTests();
        Assert.False(breaker.IsTripped); // take probe

        breaker.RecordSuccess();
        Assert.False(breaker.IsTripped);
        Assert.Equal(0, breaker.TrippedUntilMs);
        Assert.Equal(TimeSpan.FromSeconds(60), breaker.CurrentCooldown);

        // Further callers are fully open again (not stuck behind a half-open slot).
        Assert.False(breaker.IsTripped);
        Assert.False(breaker.IsTripped);
    }

    [Fact]
    public void ProviderCircuitBreaker_SuccessDuringOpenCooldown_DoesNotCloseOrResetLadder()
    {
        var transitions = new List<ProviderCircuitTransition>();
        var breaker = new ProviderCircuitBreaker("cooldown-success", transitions.Add);
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();

        var trippedUntil = breaker.TrippedUntilMs;
        var cooldown = breaker.CurrentCooldown;

        breaker.RecordSuccess();

        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
        Assert.Equal(trippedUntil, breaker.TrippedUntilMs);
        Assert.Equal(cooldown, breaker.CurrentCooldown);
        Assert.DoesNotContain(transitions, transition =>
            transition.State == ProviderCircuitTransitionState.Closed);
    }

    [Fact]
    public void ProviderCircuitBreaker_GetSnapshotReportsOpenWithCountdown()
    {
        var breaker = new ProviderCircuitBreaker("snapshot-open");
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();

        var snapshot = breaker.GetSnapshot();

        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
        Assert.NotNull(snapshot.CooldownRemainingSeconds);
        Assert.True(snapshot.CooldownRemainingSeconds > 0);
        Assert.Equal(1, snapshot.TripCount);
        Assert.Equal(3, snapshot.FailureCount);
        Assert.NotNull(snapshot.LastFailureReason);
    }

    [Fact]
    public void ProviderCircuitBreaker_GetSnapshotDoesNotClaimHalfOpenProbe()
    {
        var breaker = new ProviderCircuitBreaker("snapshot-half-open");
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.ExpireCooldownForTests();

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(ProviderCircuitState.HalfOpen, snapshot.State);

        // Snapshot reads must not consume the probe slot.
        Assert.Equal(ProviderCircuitState.HalfOpen, breaker.GetSnapshot().State);
        Assert.False(breaker.IsTripped);
    }

    [Fact]
    public void ProviderCircuitBreaker_RecordArticleNotFoundCountsMissWithoutTripping()
    {
        var breaker = new ProviderCircuitBreaker("article-miss");
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordArticleNotFound();
        breaker.RecordArticleNotFound();

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(ProviderCircuitState.Closed, snapshot.State);
        Assert.Equal(2, snapshot.ArticleMissCount);
        Assert.Equal(2, snapshot.FailureCount);
    }

    [Fact]
    public void ProviderCircuitBreaker_AbandonedProbeCanBeRetaken()
    {
        var breaker = new ProviderCircuitBreaker("probe-abandon")
        {
            ProbeAbandonTimeout = TimeSpan.FromMilliseconds(50),
        };
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.ExpireCooldownForTests();

        Assert.False(breaker.IsTripped); // first probe, then abandoned
        Assert.True(breaker.IsTripped);

        Thread.Sleep(80);
        Assert.False(breaker.IsTripped); // reclaimed
        Assert.True(breaker.IsTripped);
    }

    [Fact]
    public async Task PrioritizedSemaphore_UpdateMaxAllowed_WakesHighPriorityWaitersFirst()
    {
        using var semaphore = new PrioritizedSemaphore(initialAllowed: 1, maxAllowed: 1);
        await semaphore.WaitAsync(SemaphorePriority.High);

        var high1 = semaphore.WaitAsync(SemaphorePriority.High);
        var high2 = semaphore.WaitAsync(SemaphorePriority.High);
        var low = semaphore.WaitAsync(SemaphorePriority.Low);
        await Task.Delay(20);
        Assert.False(high1.IsCompleted);
        Assert.False(high2.IsCompleted);
        Assert.False(low.IsCompleted);

        semaphore.UpdateMaxAllowed(3);
        await high1.WaitAsync(TimeSpan.FromSeconds(1));
        await high2.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(low.IsCompleted);

        semaphore.Release();
        await low.WaitAsync(TimeSpan.FromSeconds(1));
        semaphore.Release();
        semaphore.Release();
        semaphore.Release();
    }

    [Fact]
    public async Task PrioritizedSemaphore_UpdateMaxAllowed_WakesQueuedWaiterWithZeroEntered()
    {
        using var semaphore = new PrioritizedSemaphore(initialAllowed: 0, maxAllowed: 1);
        var waiter = semaphore.WaitAsync(SemaphorePriority.High);
        await Task.Delay(20);
        Assert.False(waiter.IsCompleted);

        semaphore.UpdateMaxAllowed(2);
        await waiter.WaitAsync(TimeSpan.FromSeconds(1));
        semaphore.Release();
    }

    [Fact]
    public async Task PrioritizedSemaphore_UpdateMaxAllowed_SkipsCanceledWaiters()
    {
        using var semaphore = new PrioritizedSemaphore(initialAllowed: 0, maxAllowed: 0);
        using var cts = new CancellationTokenSource();
        var canceled = semaphore.WaitAsync(SemaphorePriority.High, cts.Token);
        var live = semaphore.WaitAsync(SemaphorePriority.High);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        semaphore.UpdateMaxAllowed(1);
        await live.WaitAsync(TimeSpan.FromSeconds(1));
        semaphore.Release();
    }

    [Fact]
    public async Task PrioritizedSemaphore_UpdateMaxAllowed_LowerThenRaiseWakesWaiters()
    {
        using var semaphore = new PrioritizedSemaphore(initialAllowed: 1, maxAllowed: 1);
        await semaphore.WaitAsync(SemaphorePriority.High);
        var waiter = semaphore.WaitAsync(SemaphorePriority.Low);
        await Task.Delay(20);

        semaphore.UpdateMaxAllowed(0);
        Assert.False(waiter.IsCompleted);

        // Release while over-capacity is absorbed (does not wake).
        semaphore.Release();
        Assert.False(waiter.IsCompleted);

        semaphore.UpdateMaxAllowed(2);
        await waiter.WaitAsync(TimeSpan.FromSeconds(1));
        semaphore.Release();
    }

    [Fact]
    public async Task PrioritizedSemaphore_BlocksUntilPermitIsReleased()
    {
        using var semaphore = new PrioritizedSemaphore(initialAllowed: 1, maxAllowed: 1);
        await semaphore.WaitAsync(SemaphorePriority.High);
        var waiter = semaphore.WaitAsync(SemaphorePriority.Low);

        Assert.False(waiter.IsCompleted);
        semaphore.Release();
        await waiter.WaitAsync(TimeSpan.FromSeconds(1));
        semaphore.Release();
    }

    [Fact]
    public async Task PrioritizedSemaphore_80PercentHighPriority_AllowsLowWaiterEveryFifthContestedRelease()
    {
        using var semaphore = new PrioritizedSemaphore(
            initialAllowed: 1,
            maxAllowed: 1,
            new SemaphorePriorityOdds { HighPriorityOdds = 80 });
        await semaphore.WaitAsync(SemaphorePriority.Low);
        var lowWaiter = semaphore.WaitAsync(SemaphorePriority.Low);

        for (var handoff = 1; handoff <= 4; handoff++)
        {
            var highWaiter = semaphore.WaitAsync(SemaphorePriority.High);
            semaphore.Release();

            await highWaiter.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(lowWaiter.IsCompleted);
        }

        var fifthHighWaiter = semaphore.WaitAsync(SemaphorePriority.High);
        semaphore.Release();

        await lowWaiter.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(fifthHighWaiter.IsCompleted);
        semaphore.Release();
        await fifthHighWaiter.WaitAsync(TimeSpan.FromSeconds(1));
        semaphore.Release();
    }

    [Fact]
    public async Task PrioritizedSemaphore_RemovesCanceledWaiter()
    {
        using var semaphore = new PrioritizedSemaphore(initialAllowed: 0, maxAllowed: 1);
        using var cts = new CancellationTokenSource();
        var waiter = semaphore.WaitAsync(SemaphorePriority.High, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        semaphore.Release();
    }

    [Fact]
    public async Task PrioritizedSemaphore_DisposeFaultsQueuedWaiters()
    {
        var semaphore = new PrioritizedSemaphore(initialAllowed: 0, maxAllowed: 1);
        var waiter = semaphore.WaitAsync(SemaphorePriority.High);

        semaphore.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiter);
    }

    [Fact]
    public void PrioritizedSemaphore_ReleaseAfterDispose_DoesNotThrow()
    {
        var semaphore = new PrioritizedSemaphore(initialAllowed: 1, maxAllowed: 1);
        semaphore.Dispose();

        var exception = Record.Exception(() => semaphore.Release());
        Assert.Null(exception);
    }

    [Fact]
    public async Task PrioritizedSemaphore_OverRelease_DoesNotThrow_AndWaitStillWorks()
    {
        using var semaphore = new PrioritizedSemaphore(initialAllowed: 1, maxAllowed: 1);
        semaphore.Release(); // over-release with nobody entered beyond the free permit

        var exception = Record.Exception(() => semaphore.Release());
        Assert.Null(exception);

        await semaphore.WaitAsync(SemaphorePriority.High);
        semaphore.Release();
    }
}
