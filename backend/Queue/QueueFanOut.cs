using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Queue;

/// <summary>
/// Resolves per-phase NNTP task fan-out for queue processing.
/// </summary>
public static class QueueFanOut
{
    /// <summary>
    /// Fan-out used by a lone primary queue item:
    /// <c>min(maxQueueConnections + 5, 50)</c>.
    /// </summary>
    public static int PrimaryFanOut(int maxQueueConnections) =>
        Math.Min(maxQueueConnections + 5, 50);

    /// <summary>
    /// When secondaries are active, leave at least one soft-budget slot per
    /// secondary so High-lane primary waiters cannot occupy the entire queue
    /// semaphore. Without this, 100% High odds starve Low-lane secondaries.
    /// </summary>
    public static int PrimaryFanOutWhenSharing(int maxQueueConnections, int activeSecondaryCount)
    {
        var reserve = Math.Max(
            Math.Max(1, activeSecondaryCount),
            Math.Max(1, maxQueueConnections / 8));
        return Math.Max(1, maxQueueConnections - reserve);
    }

    /// <summary>
    /// Secondary workers share the queue budget:
    /// <c>max(1, ceil(maxQueue / secondaryCount))</c>.
    /// </summary>
    public static int SecondaryFanOut(int maxQueueConnections, int activeSecondaryCount) =>
        Math.Max(1, (int)Math.Ceiling(maxQueueConnections / (double)Math.Max(1, activeSecondaryCount)));

    /// <summary>
    /// Reads <see cref="QueueDownloadContext"/> from <paramref name="ct"/> when present;
    /// otherwise falls back to primary fan-out (single-item / test paths).
    /// </summary>
    public static int GetConcurrency(ConfigManager configManager, CancellationToken ct)
    {
        var ctx = ct.GetContext<QueueDownloadContext>();
        if (ctx is not null)
            return Math.Max(1, ctx.GetFanOutConcurrency());
        return PrimaryFanOut(configManager.GetMaxQueueConnections());
    }

    /// <summary>
    /// Fan-out for SevenZip size population — never applies the primary +5 overshoot.
    /// </summary>
    public static int GetExactQueueConcurrency(ConfigManager configManager, CancellationToken ct)
    {
        var maxQueue = Math.Max(1, configManager.GetMaxQueueConnections());
        var ctx = ct.GetContext<QueueDownloadContext>();
        if (ctx is null)
            return maxQueue;

        // Clamp live fan-out to maxQueue so solo-primary BODY overshoot (+5) is not
        // reused for exact-budget work like SevenZip size population.
        return Math.Min(Math.Max(1, ctx.GetFanOutConcurrency()), maxQueue);
    }
}
