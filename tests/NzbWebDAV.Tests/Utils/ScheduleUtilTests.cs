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
}
