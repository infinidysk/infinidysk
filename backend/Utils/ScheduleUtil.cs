namespace NzbWebDAV.Utils;

internal static class ScheduleUtil
{
    // <=30m slices re-read the wall clock, so a DST/NTP step mid-wait can't fire early or be missed.
    private static readonly TimeSpan MaxSleepSlice = TimeSpan.FromMinutes(30);

    // Task.Delay truncates to whole milliseconds; a sub-millisecond remainder would otherwise spin.
    private static readonly TimeSpan MinSleep = TimeSpan.FromMilliseconds(1);

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
}
