namespace NzbWebDAV.Utils;

internal static class ScheduleUtil
{
    // <=30m slices re-read the wall clock, so a DST/NTP step mid-wait can't fire early or be missed.
    private static readonly TimeSpan MaxSleepSlice = TimeSpan.FromMinutes(30);

    // Task.Delay truncates to whole milliseconds; a sub-millisecond remainder would otherwise spin.
    private static readonly TimeSpan MinSleep = TimeSpan.FromMilliseconds(1);

    // Pause between attempts while another maintenance task holds BaseTask's shared slot.
    internal static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMinutes(5);

    public static async Task DelayUntilAsync(
        DateTime localTarget,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = localTarget - timeProvider.GetLocalNow().DateTime;
            if (remaining <= TimeSpan.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            var delay = remaining < MaxSleepSlice ? remaining : MaxSleepSlice;
            if (delay < MinSleep)
                delay = MinSleep;

            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    // Stays on the caller's slot until tryExecute runs; recomputing from the clock would pick tomorrow.
    public static async Task RetryWhileBusyAsync(
        Func<Task<bool>> tryExecute,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        do
        {
            await Task.Delay(BusyRetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        while (!await tryExecute().ConfigureAwait(false));
    }
}
