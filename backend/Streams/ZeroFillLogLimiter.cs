using System.Collections.Concurrent;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Coalesces zero-fill warnings by file so a release with many unavailable
/// articles cannot flood the application log.
/// </summary>
internal static class ZeroFillLogLimiter
{
    private static readonly ConcurrentDictionary<string, WindowState> Windows =
        new(StringComparer.Ordinal);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CleanupThreshold = TimeSpan.FromMinutes(5);
    private static int _callCount;

    /// <param name="context">
    /// Optional diagnostic detail appended as its own property, so a warning explains
    /// which part of which file fell short without needing a second log line.
    /// </param>
    public static void Write(
        string messageTemplate,
        string segmentId,
        string fileName,
        long bytes,
        Exception? exception = null,
        string? context = null)
    {
        var now = DateTime.UtcNow;
        var state = Windows.GetOrAdd(fileName, static _ => new WindowState());
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

        if (shouldLog)
        {
            if (suppressed > 0)
            {
                Log.Warning(
                    "Suppressed {SuppressedCount} additional zero-fill warnings for {FileName} in the previous 60 seconds.",
                    suppressed,
                    fileName);
            }

            var template = context is null ? messageTemplate : messageTemplate + " {Context}";
            object?[] values = context is null
                ? [segmentId, fileName, bytes]
                : [segmentId, fileName, bytes, context];

            if (exception is null)
                Log.Warning(template, values);
            else
                exception.LogWarningKnownOrStack(template, values);
        }

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
