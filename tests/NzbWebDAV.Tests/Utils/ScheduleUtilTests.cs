using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class ScheduleUtilTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DelayUntilAsync_TimerWakesBeforeTarget_KeepsWaitingForSameTarget()
    {
        var clock = new ControllableTimeProvider();
        // 1.5 ms truncates to a 1 ms timer, which wakes 0.5 ms before the target.
        var target = clock.GetLocalNow().DateTime + TimeSpan.FromTicks(15_000);

        var waiting = ScheduleUtil.DelayUntilAsync(target, clock, CancellationToken.None);

        await Task.Run(() => clock.Advance(TimeSpan.FromMilliseconds(1))).WaitAsync(WaitBudget);
        Assert.True(SpinWait.SpinUntil(() => clock.HasScheduledTimer || waiting.IsCompleted, WaitBudget));
        Assert.False(waiting.IsCompleted);

        await Task.Run(() => clock.Advance(TimeSpan.FromMilliseconds(1))).WaitAsync(WaitBudget);
        await waiting.WaitAsync(WaitBudget);
    }

    [Fact]
    public async Task RetryWhileBusyAsync_SlotStillBusy_RetriesSameSlotUntilTaskRuns()
    {
        var clock = new ControllableTimeProvider();
        var attempts = 0;
        // Busy on the first retry, free on the second.
        Task<bool> TryExecute() => Task.FromResult(Interlocked.Increment(ref attempts) == 2);

        var retrying = ScheduleUtil.RetryWhileBusyAsync(TryExecute, clock, CancellationToken.None);

        await Task.Run(() => clock.Advance(ScheduleUtil.BusyRetryDelay)).WaitAsync(WaitBudget);
        Assert.True(SpinWait.SpinUntil(() => clock.HasScheduledTimer || retrying.IsCompleted, WaitBudget));
        Assert.False(retrying.IsCompleted);

        await Task.Run(() => clock.Advance(ScheduleUtil.BusyRetryDelay)).WaitAsync(WaitBudget);
        await retrying.WaitAsync(WaitBudget);
        Assert.Equal(2, Volatile.Read(ref attempts));
    }
}
