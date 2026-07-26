using System.Collections.Concurrent;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Coalesces repeated operator warnings for the same provider/segment/file key so a
/// stuck corrupt article cannot flood the application log.
/// </summary>
internal static class ThrottledSegmentWarning
{
    private static readonly ConcurrentDictionary<string, WindowState> Windows =
        new(StringComparer.Ordinal);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CleanupThreshold = TimeSpan.FromMinutes(5);
    private static int _callCount;

    public static void Write(
        string key,
        string messageTemplate,
        params object?[] propertyValues)
    {
        var now = DateTime.UtcNow;
        var state = Windows.GetOrAdd(key, static _ => new WindowState());
        var shouldLog = false;
        var suppressed = 0;

        lock (state)
        {
            if (state.WindowStarted == default || now - state.WindowStarted >= Window)
            {
                suppressed = state.Suppressed;
                state.WindowStarted = now;
                state.Suppressed = 0;
                shouldLog = true;
            }
            else
            {
                state.Suppressed++;
            }
        }

        if (!shouldLog) return;

        if (suppressed > 0)
        {
            Log.Warning(
                "Suppressed {SuppressedCount} additional warnings for {WarningKey} in the previous 60 seconds.",
                suppressed,
                key);
        }

        Log.Warning(messageTemplate, propertyValues);

        if (Interlocked.Increment(ref _callCount) % 256 == 0)
            Cleanup(now);
    }

    private static void Cleanup(DateTime now)
    {
        foreach (var entry in Windows)
        {
            lock (entry.Value)
            {
                if (now - entry.Value.WindowStarted >= CleanupThreshold)
                    Windows.TryRemove(entry.Key, out _);
            }
        }
    }

    private sealed class WindowState
    {
        public DateTime WindowStarted { get; set; }
        public int Suppressed { get; set; }
    }
}
