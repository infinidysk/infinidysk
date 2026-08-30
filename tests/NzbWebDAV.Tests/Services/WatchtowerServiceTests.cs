using System.Diagnostics;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class WatchtowerServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Watchdog_CooperativeCancellationDrainsBeforeNextTick()
    {
        var clock = new ControllableTimeProvider();
        var scripted = new ScriptedCycle();
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);
        await using var run = Start(clock, scripted.CooperativeCancelAsync);

        await scripted.FirstStarted.Task.WaitAsync(TestTimeout);
        var cycle1 = RequireActive(run.Service);

        clock.Advance(WatchtowerService.CycleWatchdogTimeout);
        await scripted.FirstExited.Task.WaitAsync(TestTimeout);
        await cycle1.WhenBothTasksObserved.WaitAsync(TestTimeout);
        await cycle1.WhenOwnerCleared.WaitAsync(TestTimeout);

        Assert.False(scripted.SecondStarted.Task.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref scripted.Starts));
        Assert.Equal(1, scripted.MaximumActive);

        await AdvanceScheduledAsync(clock, WatchtowerService.CycleInterval);
        await scripted.SecondStarted.Task.WaitAsync(TestTimeout);

        Assert.Equal(1, scripted.MaximumActive);
        var warning = Assert.Single(sink.Events, IsWatchdogWarning);
        Assert.Null(warning.Exception);
        Assert.Equal(1, sink.Events.Count(IsWatchdogWarning));
    }

    [Fact]
    public async Task Watchdog_DelayedDrainPreventsCycleOverlap()
    {
        var clock = new ControllableTimeProvider();
        var scripted = new ScriptedCycle();
        await using var run = Start(clock, scripted.DelayFirstCancellationAsync);
        try
        {
            await scripted.FirstStarted.Task.WaitAsync(TestTimeout);
            var cycle1 = RequireActive(run.Service);

            clock.Advance(WatchtowerService.CycleWatchdogTimeout);
            await scripted.FirstCancellation.Task.WaitAsync(TestTimeout);

            Assert.Equal(1, Volatile.Read(ref scripted.Starts));
            Assert.Equal(1, scripted.MaximumActive);
            Assert.False(scripted.SecondStarted.Task.IsCompleted);
            Assert.False(run.ExecuteTask.IsCompleted);
            Assert.Same(cycle1, run.Service.ActiveCycleForTests);

            clock.Advance(TimeSpan.FromHours(1));
            Assert.False(scripted.SecondStarted.Task.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref scripted.Starts));

            scripted.ReleaseFirst.TrySetResult();
            await scripted.FirstExited.Task.WaitAsync(TestTimeout);
            await cycle1.WhenBothTasksObserved.WaitAsync(TestTimeout);
            await cycle1.WhenOwnerCleared.WaitAsync(TestTimeout);

            await AdvanceScheduledAsync(clock, WatchtowerService.CycleInterval);
            await scripted.SecondStarted.Task.WaitAsync(TestTimeout);
            Assert.Equal(1, scripted.MaximumActive);
        }
        finally
        {
            scripted.ReleaseFirst.TrySetResult();
        }
    }

    [Fact]
    public async Task CycleFirst_DisableDuringCallbackDrainAwaitsStoredCancellationTask()
    {
        var clock = new ControllableTimeProvider();
        var callbackStarted = NewSignal();
        var releaseCallback = NewSignal();
        var firstReturned = NewSignal();
        var secondStarted = NewSignal();
        var starts = 0;
        var callbackCount = 0;
        using var storedReg = new StoredRegistration();

        await using var run = Start(clock, async (_, ct) =>
        {
            var ordinal = Interlocked.Increment(ref starts);
            if (ordinal == 1)
            {
                storedReg.Registration = ct.Register(() =>
                {
                    Interlocked.Increment(ref callbackCount);
                    callbackStarted.TrySetResult();
                    releaseCallback.Task.GetAwaiter().GetResult();
                });
                firstReturned.TrySetResult();
                return;
            }

            secondStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        try
        {
            await firstReturned.Task.WaitAsync(TestTimeout);
            await callbackStarted.Task.WaitAsync(TestTimeout);
            var active = RequireActive(run.Service);
            var cancellationTask = active.GetCancellationTaskOrThrow();
            Assert.False(cancellationTask.IsCompleted);

            SetEnabled(run.Config, false);
            Assert.Same(cancellationTask, active.RequestCancellation(
                WatchtowerService.CycleStopReason.Disabled));
            Assert.False(cancellationTask.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref callbackCount));
            Assert.Same(active, run.Service.ActiveCycleForTests);
            Assert.False(run.ExecuteTask.IsCompleted);
            Assert.False(active.BothTasksObserved);

            clock.Advance(TimeSpan.FromHours(3));
            Assert.Equal(1, Volatile.Read(ref starts));
            Assert.False(secondStarted.Task.IsCompleted);

            releaseCallback.TrySetResult();
            await active.WhenBothTasksObserved.WaitAsync(TestTimeout);
            await active.WhenOwnerCleared.WaitAsync(TestTimeout);
            Assert.Null(run.Service.ActiveCycleForTests);

            await AdvanceScheduledAsync(clock, WatchtowerService.AdmissionPollInterval);
            clock.Advance(WatchtowerService.AdmissionPollInterval);
            clock.Advance(WatchtowerService.CycleInterval);
            Assert.Equal(1, Volatile.Read(ref starts));
            Assert.False(secondStarted.Task.IsCompleted);
        }
        finally
        {
            releaseCallback.TrySetResult();
        }
    }

    [Fact]
    public async Task ConcurrentStopSources_ShareOneCancellationTaskAndCallbackPass()
    {
        var active = new WatchtowerService.ActiveCycle();
        using var _ = active;
        var cycleHold = NewSignal();
        active.AttachCycleTask(cycleHold.Task);

        var callbackStarted = NewSignal();
        var releaseCallback = NewSignal();
        var invocations = 0;
        using var registration = active.Token.Register(() =>
        {
            Interlocked.Increment(ref invocations);
            callbackStarted.TrySetResult();
            if (!releaseCallback.Task.Wait(TestTimeout))
                throw new TimeoutException("Callback release timed out.");
        });

        var start = NewSignal();
        var reasons = new[]
        {
            WatchtowerService.CycleStopReason.CycleFinished,
            WatchtowerService.CycleStopReason.Watchdog,
            WatchtowerService.CycleStopReason.Disabled,
            WatchtowerService.CycleStopReason.HostStopping,
        };
        var callers = reasons.Select(reason => Task.Run(async () =>
        {
            await start.Task.WaitAsync(TestTimeout);
            return active.RequestCancellation(reason);
        })).ToArray();

        try
        {
            start.TrySetResult();
            var tasks = await Task.WhenAll(callers);
            var first = tasks[0];
            Assert.All(tasks, task => Assert.Same(first, task));
            Assert.NotSame(Task.CompletedTask, first);

            await callbackStarted.Task.WaitAsync(TestTimeout);
            Assert.False(first.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref invocations));
            Assert.Equal(WatchtowerService.CycleStopReason.HostStopping, active.StopReason);
            Assert.False(active.BothTasksObserved);

            releaseCallback.TrySetResult();
            await first.WaitAsync(TestTimeout);
            Assert.False(active.BothTasksObserved);
            Assert.False(cycleHold.Task.IsCompleted);

            cycleHold.TrySetResult();
            await cycleHold.Task.WaitAsync(TestTimeout);
            Assert.False(active.BothTasksObserved);
        }
        finally
        {
            releaseCallback.TrySetResult();
            cycleHold.TrySetResult();
        }
    }

    [Fact]
    public async Task CancellationCallbackFailure_IsObservedBeforeRetry()
    {
        var clock = new ControllableTimeProvider();
        var error = new InvalidOperationException("watchtower-callback-failure");
        using var storedReg = new StoredRegistration();
        var firstReturned = NewSignal();
        var secondStarted = NewSignal();
        var starts = 0;
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);

        await using var run = Start(clock, async (_, ct) =>
        {
            var ordinal = Interlocked.Increment(ref starts);
            if (ordinal == 1)
            {
                storedReg.Registration = ct.Register(() => throw error);
                firstReturned.TrySetResult();
                return;
            }

            secondStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        await firstReturned.Task.WaitAsync(TestTimeout);
        var cycle1 = await WaitClearedAsync(run.Service);
        Assert.True(cycle1.BothTasksObserved);
        await WaitForPostCycleDelayAsync(clock);

        var failure = Assert.Single(
            sink.Events,
            e => e.Exception is not null && HasException(e.Exception, error));
        Assert.Equal(LogEventLevel.Error, failure.Level);
        Assert.Contains("unexpected cycle cancellation failure", failure.MessageTemplate.Text);
        Assert.Equal(1, sink.Events.Count(e => e.Exception is not null && HasException(e.Exception, error)));
        Assert.DoesNotContain(sink.Events, IsWatchdogWarning);
        Assert.False(secondStarted.Task.IsCompleted);

        await AdvanceScheduledAsync(clock, WatchtowerService.LoopErrorDelay);
        await secondStarted.Task.WaitAsync(TestTimeout);
        Assert.Equal(2, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task CancellationCallbackOutOfMemory_IsUnwrappedAndPropagated()
    {
        var clock = new ControllableTimeProvider();
        var oom = new OutOfMemoryException("watchtower-callback-oom");
        using var storedReg = new StoredRegistration();
        var firstReturned = NewSignal();
        var starts = 0;
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);

        await using var run = Start(clock, (_, ct) =>
        {
            Interlocked.Increment(ref starts);
            storedReg.Registration = ct.Register(() => throw oom);
            firstReturned.TrySetResult();
            return Task.CompletedTask;
        });

        await firstReturned.Task.WaitAsync(TestTimeout);
        var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(
            () => run.ExecuteTask.WaitAsync(TestTimeout));
        Assert.Same(oom, thrown);
        var cycle1 = await WaitClearedAsync(run.Service);
        Assert.True(cycle1.BothTasksObserved);
        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.DoesNotContain(sink.Events, e => e.MessageTemplate.Text.Contains("Watchtower loop error"));
        Assert.DoesNotContain(sink.Events, e => e.MessageTemplate.Text.Contains("cancellation failure"));
        Assert.DoesNotContain(sink.Events, IsWatchdogWarning);
        Assert.Null(run.Service.ActiveCycleForTests);
    }

    [Fact]
    public async Task CancellationAndCycleOutOfMemory_PropagatesCancellationOom()
    {
        var clock = new ControllableTimeProvider();
        var cancellationOom = new OutOfMemoryException("watchtower-callback-oom");
        var cycleOom = new OutOfMemoryException("watchtower-cycle-oom");
        using var storedReg = new StoredRegistration();
        var firstReturned = NewSignal();
        var starts = 0;
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);

        await using var run = Start(clock, (_, ct) =>
        {
            Interlocked.Increment(ref starts);
            storedReg.Registration = ct.Register(() => throw cancellationOom);
            firstReturned.TrySetResult();
            throw cycleOom;
        });

        await firstReturned.Task.WaitAsync(TestTimeout);
        var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(
            () => run.ExecuteTask.WaitAsync(TestTimeout));
        Assert.Same(cancellationOom, thrown);
        Assert.True((await WaitClearedAsync(run.Service)).BothTasksObserved);
        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.DoesNotContain(sink.Events, e => e.Level is LogEventLevel.Warning or LogEventLevel.Error);
    }

    [Fact]
    public async Task Watchdog_DrainFaultIsObservedWithStackBeforeRetry()
    {
        var clock = new ControllableTimeProvider();
        var error = new InvalidOperationException("watchtower-drain-fault");
        var cancelled = NewSignal();
        var secondStarted = NewSignal();
        var starts = 0;
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);

        await using var run = Start(clock, async (_, ct) =>
        {
            var ordinal = Interlocked.Increment(ref starts);
            if (ordinal != 1)
            {
                secondStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return;
            }

            using var registration = ct.Register(() => cancelled.TrySetResult());
            await cancelled.Task.WaitAsync(TestTimeout);
            throw error;
        });

        await run.WaitUntilFirstCycleAsync();
        var cycle1 = RequireActive(run.Service);
        clock.Advance(WatchtowerService.CycleWatchdogTimeout);
        await cancelled.Task.WaitAsync(TestTimeout);
        await cycle1.WhenBothTasksObserved.WaitAsync(TestTimeout);
        await cycle1.WhenOwnerCleared.WaitAsync(TestTimeout);
        await WaitForPostCycleDelayAsync(clock);

        var warning = Assert.Single(sink.Events, IsWatchdogWarning);
        Assert.Null(warning.Exception);
        var drainFault = Assert.Single(
            sink.Events,
            e => e.Exception is not null && HasException(e.Exception, error));
        Assert.Equal(LogEventLevel.Warning, drainFault.Level);
        Assert.Contains("faulted while draining", drainFault.MessageTemplate.Text);
        Assert.Equal(1, sink.Events.Count(e => e.Exception is not null && HasException(e.Exception, error)));
        Assert.False(secondStarted.Task.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref starts));

        await AdvanceScheduledAsync(clock, WatchtowerService.LoopErrorDelay);
        await secondStarted.Task.WaitAsync(TestTimeout);
        Assert.Equal(2, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task NaturalCycleFault_UsesKnownErrorLoggingAndRetryDelay()
    {
        var clock = new ControllableTimeProvider();
        var error = new IOException("watchtower-known-cycle-fault");
        var secondStarted = NewSignal();
        var starts = 0;
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);

        await using var run = Start(clock, async (_, ct) =>
        {
            var ordinal = Interlocked.Increment(ref starts);
            if (ordinal == 1)
                throw error;

            secondStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        var cycle1 = await WaitClearedAsync(run.Service);
        Assert.True(cycle1.BothTasksObserved);
        await WaitForPostCycleDelayAsync(clock);

        var warning = Assert.Single(
            sink.Events,
            e => e.MessageTemplate.Text.Contains("Watchtower loop error"));
        Assert.Null(warning.Exception);
        Assert.Equal(error.Message, warning.Properties["Reason"].LiteralValue());
        Assert.DoesNotContain(sink.Events, IsWatchdogWarning);
        Assert.False(secondStarted.Task.IsCompleted);

        await AdvanceScheduledAsync(clock, WatchtowerService.LoopErrorDelay);
        await secondStarted.Task.WaitAsync(TestTimeout);
        Assert.Equal(2, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task HostShutdown_DrainsActiveCycleAndNeverStartsReplacement()
    {
        var clock = new ControllableTimeProvider();
        var scripted = new ScriptedCycle();
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);
        await using var run = Start(clock, scripted.DelayFirstCancellationAsync);
        try
        {
            await scripted.FirstStarted.Task.WaitAsync(TestTimeout);
            await run.HostCts.CancelAsync();
            await scripted.FirstCancellation.Task.WaitAsync(TestTimeout);

            Assert.False(run.ExecuteTask.IsCompleted);
            clock.Advance(WatchtowerService.CycleWatchdogTimeout);
            clock.Advance(WatchtowerService.CycleInterval);
            clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(1, Volatile.Read(ref scripted.Starts));
            Assert.False(scripted.SecondStarted.Task.IsCompleted);

            scripted.ReleaseFirst.TrySetResult();
            await run.ExecuteTask.WaitAsync(TestTimeout);
            Assert.Equal(1, Volatile.Read(ref scripted.Starts));
            Assert.DoesNotContain(sink.Events, IsWatchdogWarning);
        }
        finally
        {
            scripted.ReleaseFirst.TrySetResult();
        }
    }

    [Fact]
    public async Task HostShutdown_DuringWatchdogDrainSuppressesRestart()
    {
        var clock = new ControllableTimeProvider();
        var scripted = new ScriptedCycle();
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);
        await using var run = Start(clock, scripted.DelayFirstCancellationAsync);
        try
        {
            await scripted.FirstStarted.Task.WaitAsync(TestTimeout);
            clock.Advance(WatchtowerService.CycleWatchdogTimeout);
            await scripted.FirstCancellation.Task.WaitAsync(TestTimeout);
            Assert.Contains(sink.Events, IsWatchdogWarning);

            await run.HostCts.CancelAsync();
            Assert.False(run.ExecuteTask.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref scripted.Starts));

            scripted.ReleaseFirst.TrySetResult();
            await run.ExecuteTask.WaitAsync(TestTimeout);
            Assert.Equal(1, Volatile.Read(ref scripted.Starts));
            Assert.False(scripted.SecondStarted.Task.IsCompleted);
        }
        finally
        {
            scripted.ReleaseFirst.TrySetResult();
        }
    }

    [Fact]
    public async Task Disable_DrainsActiveCycleAndDoesNotRestartWhileDisabled()
    {
        var clock = new ControllableTimeProvider();
        var scripted = new ScriptedCycle();
        await using var run = Start(clock, scripted.DelayFirstCancellationAsync);
        try
        {
            await scripted.FirstStarted.Task.WaitAsync(TestTimeout);
            var cycle1 = RequireActive(run.Service);
            SetEnabled(run.Config, false);
            await scripted.FirstCancellation.Task.WaitAsync(TestTimeout);
            Assert.Equal(1, Volatile.Read(ref scripted.Starts));
            Assert.False(scripted.SecondStarted.Task.IsCompleted);

            scripted.ReleaseFirst.TrySetResult();
            await cycle1.WhenOwnerCleared.WaitAsync(TestTimeout);
            await AdvanceScheduledAsync(clock, WatchtowerService.AdmissionPollInterval);
            clock.Advance(WatchtowerService.AdmissionPollInterval);
            clock.Advance(WatchtowerService.CycleInterval);
            Assert.Equal(1, Volatile.Read(ref scripted.Starts));
            Assert.False(scripted.SecondStarted.Task.IsCompleted);

            SetEnabled(run.Config, true);
            await AdvanceScheduledAsync(clock, WatchtowerService.AdmissionPollInterval);
            await scripted.SecondStarted.Task.WaitAsync(TestTimeout);
            Assert.Equal(2, Volatile.Read(ref scripted.Starts));
            Assert.Equal(1, scripted.MaximumActive);
        }
        finally
        {
            scripted.ReleaseFirst.TrySetResult();
        }
    }

    [Fact]
    public async Task Watchdog_NoncooperativeCycleRetainsOwnershipUntilExplicitTestCleanup()
    {
        var clock = new ControllableTimeProvider();
        var scripted = new ScriptedCycle();
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);
        await using var run = Start(clock, scripted.DelayFirstCancellationAsync);
        try
        {
            await scripted.FirstStarted.Task.WaitAsync(TestTimeout);
            var cycle1 = RequireActive(run.Service);
            clock.Advance(WatchtowerService.CycleWatchdogTimeout);
            await scripted.FirstCancellation.Task.WaitAsync(TestTimeout);

            clock.Advance(TimeSpan.FromHours(6));
            Assert.False(run.ExecuteTask.IsCompleted);
            Assert.Same(cycle1, run.Service.ActiveCycleForTests);
            Assert.Equal(1, Volatile.Read(ref scripted.Starts));
            Assert.Equal(1, sink.Events.Count(IsWatchdogWarning));
            Assert.False(scripted.SecondStarted.Task.IsCompleted);

            scripted.ReleaseFirst.TrySetResult();
            await cycle1.WhenOwnerCleared.WaitAsync(TestTimeout);
        }
        finally
        {
            scripted.ReleaseFirst.TrySetResult();
        }
    }

    [Fact]
    public async Task Watchdog_NoncooperativeCycleAwaitsIndefinitelyWithoutReplacement()
    {
        var clock = new ControllableTimeProvider();
        var scripted = new ScriptedCycle();
        await using var run = Start(clock, scripted.DelayFirstCancellationAsync);
        try
        {
            await scripted.FirstStarted.Task.WaitAsync(TestTimeout);
            clock.Advance(WatchtowerService.CycleWatchdogTimeout);
            await scripted.FirstCancellation.Task.WaitAsync(TestTimeout);

            await run.HostCts.CancelAsync();
            clock.Advance(TimeSpan.FromHours(2));
            Assert.False(run.ExecuteTask.IsCompleted);
            Assert.NotNull(run.Service.ActiveCycleForTests);
            Assert.Equal(1, Volatile.Read(ref scripted.Starts));

            scripted.ReleaseFirst.TrySetResult();
            await run.ExecuteTask.WaitAsync(TestTimeout);
            Assert.Equal(1, Volatile.Read(ref scripted.Starts));
            Assert.False(scripted.SecondStarted.Task.IsCompleted);
        }
        finally
        {
            scripted.ReleaseFirst.TrySetResult();
        }
    }

    [Fact]
    public async Task CycleOutOfMemory_FaultsHostedExecutionWithoutRestart()
    {
        var clock = new ControllableTimeProvider();
        var oom = new OutOfMemoryException("watchtower-cycle-oom");
        var starts = 0;
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);

        await using var run = Start(clock, (_, _) =>
        {
            Interlocked.Increment(ref starts);
            throw oom;
        });

        var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(
            () => run.ExecuteTask.WaitAsync(TestTimeout));
        Assert.Same(oom, thrown);
        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.DoesNotContain(sink.Events, e => e.MessageTemplate.Text.Contains("Watchtower loop error"));
        Assert.DoesNotContain(sink.Events, IsWatchdogWarning);
    }

    [Fact]
    public async Task Watchdog_CycleOutOfMemoryDuringDrainFaultsHostedExecution()
    {
        var clock = new ControllableTimeProvider();
        var oom = new OutOfMemoryException("watchtower-drain-oom");
        var cancelled = NewSignal();
        var starts = 0;
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);

        await using var run = Start(clock, async (_, ct) =>
        {
            Interlocked.Increment(ref starts);
            using var registration = ct.Register(() => cancelled.TrySetResult());
            await cancelled.Task.WaitAsync(TestTimeout);
            throw oom;
        });

        await run.WaitUntilFirstCycleAsync();
        clock.Advance(WatchtowerService.CycleWatchdogTimeout);
        await cancelled.Task.WaitAsync(TestTimeout);

        var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(
            () => run.ExecuteTask.WaitAsync(TestTimeout));
        Assert.Same(oom, thrown);
        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.Contains(sink.Events, IsWatchdogWarning);
        Assert.DoesNotContain(sink.Events, e => e.MessageTemplate.Text.Contains("Watchtower loop error"));
        Assert.DoesNotContain(sink.Events, e => e.MessageTemplate.Text.Contains("faulted while draining"));
    }

    [Fact]
    public async Task CompletedCycle_CancelsWatchdogDelayAndIgnoresLaterConfigDisable()
    {
        var clock = new ControllableTimeProvider();
        var firstReturned = NewSignal();
        var secondStarted = NewSignal();
        var starts = 0;
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);

        await using var run = Start(clock, async (_, ct) =>
        {
            var ordinal = Interlocked.Increment(ref starts);
            if (ordinal == 1)
            {
                firstReturned.TrySetResult();
                return;
            }

            secondStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        await firstReturned.Task.WaitAsync(TestTimeout);
        var cycle1 = await WaitClearedAsync(run.Service);
        Assert.True(cycle1.BothTasksObserved);
        Assert.Null(run.Service.ActiveCycleForTests);

        SetEnabled(run.Config, false);
        clock.Advance(WatchtowerService.CycleWatchdogTimeout);
        clock.Advance(TimeSpan.FromHours(1));

        Assert.DoesNotContain(sink.Events, IsWatchdogWarning);
        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.False(secondStarted.Task.IsCompleted);
        Assert.False(run.ExecuteTask.IsFaulted);
    }

    [Fact]
    public async Task CancellationAndCycleFailures_AreAggregatedInSourceOrder()
    {
        var clock = new ControllableTimeProvider();
        var callbackError = new InvalidOperationException("watchtower-callback-aggregate");
        var cycleError = new InvalidOperationException("watchtower-cycle-aggregate");
        using var storedReg = new StoredRegistration();
        var firstReturned = NewSignal();
        var sink = new CollectingSink();
        using var logs = CaptureLogs(sink);

        await using var run = Start(clock, (_, ct) =>
        {
            storedReg.Registration = ct.Register(() => throw callbackError);
            firstReturned.TrySetResult();
            throw cycleError;
        });

        await firstReturned.Task.WaitAsync(TestTimeout);
        await WaitClearedAsync(run.Service);
        await WaitForPostCycleDelayAsync(clock);

        var failure = Assert.Single(
            sink.Events,
            e => e.Exception is not null && HasException(e.Exception, callbackError));
        Assert.True(HasException(failure.Exception!, cycleError));
        var aggregate = Assert.IsType<AggregateException>(failure.Exception);
        var leaves = aggregate.Flatten().InnerExceptions;
        Assert.Same(callbackError, leaves.First(ex => ReferenceEquals(ex, callbackError)));
        Assert.Same(cycleError, leaves.First(ex => ReferenceEquals(ex, cycleError)));
        Assert.True(
            IndexOf(leaves, callbackError) < IndexOf(leaves, cycleError),
            aggregate.ToString());
    }

    private static HostedRun Start(
        ControllableTimeProvider clock,
        Func<Stopwatch, CancellationToken, Task> runCycle)
    {
        var config = new ConfigManager();
        SetEnabled(config, true);
        var service = new WatchtowerService(
            config,
            searchProfileService: null!,
            fastVerifier: null!,
            hitTracker: null!,
            rateLimiter: null!,
            negativeCache: null!,
            wardenStore: null!,
            preflightCache: null!,
            enumerator: null!,
            episodeEnumerator: null!,
            preferredOrderStore: null!,
            nzbFetchCoalescer: null!,
            benchmarkGate: new BenchmarkGate(),
            dbContextFactory: null!,
            timeProvider: clock)
        {
            RunCycleOverride = runCycle,
        };
        var hostCts = new CancellationTokenSource();
        return new HostedRun(service, config, hostCts, service.ExecuteHostedServiceForTests(hostCts.Token));
    }

    private static void SetEnabled(ConfigManager config, bool enabled) =>
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.WatchtowerEnabled,
                ConfigValue = enabled ? "true" : "false",
            },
        ]);

    private static WatchtowerService.ActiveCycle RequireActive(WatchtowerService service)
    {
        var active = service.ActiveCycleForTests;
        Assert.NotNull(active);
        return active!;
    }

    private static async Task<WatchtowerService.ActiveCycle> WaitClearedAsync(WatchtowerService service)
    {
        await WaitUntilAsync(
            () => service.LastClearedCycleForTests is not null,
            "Watchtower never cleared the active cycle");
        var cleared = service.LastClearedCycleForTests!;
        await cleared.WhenBothTasksObserved.WaitAsync(TestTimeout);
        await cleared.WhenOwnerCleared.WaitAsync(TestTimeout);
        return cleared;
    }

    private static async Task WaitForPostCycleDelayAsync(ControllableTimeProvider clock) =>
        await WaitUntilAsync(
            () => clock.HasScheduledTimer,
            "Watchtower did not schedule a post-cycle delay");

    private static async Task AdvanceScheduledAsync(ControllableTimeProvider clock, TimeSpan delay)
    {
        await WaitForPostCycleDelayAsync(clock);
        clock.Advance(delay);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TestTimeout)
                throw new TimeoutException(message);
            await Task.Yield();
        }
    }

    private static bool IsWatchdogWarning(LogEvent logEvent) =>
        logEvent.Level == LogEventLevel.Warning
        && logEvent.MessageTemplate.Text.Contains("cancellation requested, waiting for completion");

    private static bool HasException(Exception haystack, Exception needle) =>
        Flatten(haystack).Any(current => ReferenceEquals(current, needle));

    private static int IndexOf(IReadOnlyList<Exception> exceptions, Exception needle)
    {
        for (var i = 0; i < exceptions.Count; i++)
        {
            if (ReferenceEquals(exceptions[i], needle))
                return i;
        }

        return int.MaxValue;
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        var stack = new Stack<Exception>();
        stack.Push(exception);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                for (var i = aggregate.InnerExceptions.Count - 1; i >= 0; i--)
                    stack.Push(aggregate.InnerExceptions[i]);
            }
            else if (current.InnerException is { } inner)
            {
                stack.Push(inner);
            }
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static LoggerCapture CaptureLogs(CollectingSink sink)
    {
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return new LoggerCapture(previous);
    }

    private sealed class LoggerCapture(ILogger previous) : IDisposable
    {
        public void Dispose() => Log.Logger = previous;
    }

    private sealed class StoredRegistration : IDisposable
    {
        public CancellationTokenRegistration Registration { get; set; }

        public void Dispose() => Registration.Dispose();
    }

    private sealed class HostedRun(
        WatchtowerService service,
        ConfigManager config,
        CancellationTokenSource hostCts,
        Task executeTask) : IAsyncDisposable
    {
        public WatchtowerService Service { get; } = service;
        public ConfigManager Config { get; } = config;
        public CancellationTokenSource HostCts { get; } = hostCts;
        public Task ExecuteTask { get; } = executeTask;

        public async Task WaitUntilFirstCycleAsync() =>
            await WaitUntilAsync(
                () => Service.ActiveCycleForTests is not null || ExecuteTask.IsCompleted,
                "Watchtower never published an active cycle");

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!HostCts.IsCancellationRequested)
                    await HostCts.CancelAsync();

                if (ExecuteTask.IsCompleted)
                {
                    _ = ExecuteTask.Exception;
                    return;
                }

                try
                {
                    await ExecuteTask.WaitAsync(TestTimeout);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Faulted hosted execution is asserted by the test body.
                }
            }
            finally
            {
                Service.Dispose();
                HostCts.Dispose();
            }
        }
    }

    private sealed class ScriptedCycle
    {
        private int _active;
        private int _maximumActive;

        public int Starts;
        public int MaximumActive => Volatile.Read(ref _maximumActive);

        public TaskCompletionSource FirstStarted { get; } = NewSignal();
        public TaskCompletionSource FirstCancellation { get; } = NewSignal();
        public TaskCompletionSource ReleaseFirst { get; } = NewSignal();
        public TaskCompletionSource FirstExited { get; } = NewSignal();
        public TaskCompletionSource SecondStarted { get; } = NewSignal();

        public async Task DelayFirstCancellationAsync(
            Stopwatch _,
            CancellationToken cancellationToken)
        {
            var ordinal = Begin();
            try
            {
                if (ordinal == 1)
                {
                    using var registration = cancellationToken.Register(
                        static state => ((TaskCompletionSource)state!).TrySetResult(),
                        FirstCancellation);
                    FirstStarted.TrySetResult();
                    await ReleaseFirst.Task.ConfigureAwait(false);
                    return;
                }

                SecondStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                End(ordinal);
            }
        }

        public async Task CooperativeCancelAsync(
            Stopwatch _,
            CancellationToken cancellationToken)
        {
            var ordinal = Begin();
            try
            {
                if (ordinal == 1)
                {
                    FirstStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return;
                }

                SecondStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                End(ordinal);
            }
        }

        private int Begin()
        {
            var ordinal = Interlocked.Increment(ref Starts);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maximumActive, active);
            return ordinal;
        }

        private void End(int ordinal)
        {
            Interlocked.Decrement(ref _active);
            if (ordinal == 1)
                FirstExited.TrySetResult();
        }

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximum);
                if (candidate <= current)
                    return;
                if (Interlocked.CompareExchange(ref maximum, candidate, current) == current)
                    return;
            }
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events)
                    return _events.ToList();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }
    }
}

file static class WatchtowerLogEventPropertyValueExtensions
{
    public static object? LiteralValue(this LogEventPropertyValue value) =>
        value is ScalarValue scalar ? scalar.Value : value.ToString();
}
