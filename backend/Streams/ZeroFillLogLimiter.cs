using System.Collections.Concurrent;
using System.Text;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Coalesces per-file warnings so a release with many unavailable articles
/// cannot flood the application log. Prefetch-miss and gap-fill warnings share
/// the same window so one file cannot emit both at full rate.
/// </summary>
internal static class ZeroFillLogLimiter
{
    private static readonly ConcurrentDictionary<string, WindowState> Windows =
        new(StringComparer.Ordinal);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CleanupThreshold = TimeSpan.FromMinutes(5);
    private static int _callCount;

    internal static void ResetForTests()
    {
        Windows.Clear();
        Volatile.Write(ref _callCount, 0);
    }

    /// <summary>
    /// Returns true when this file should emit a warning. <paramref name="suppressed"/>
    /// is the count hidden during the previous window (0 on the first emission).
    /// Unattributed files always log so queue/STAT noise is not silently dropped.
    /// </summary>
    public static bool TryLog(string? fileName, out int suppressed)
    {
        suppressed = 0;
        if (string.IsNullOrEmpty(fileName) || fileName == "unknown")
            return true;

        var baseName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(baseName) || baseName == "unknown")
            return true;

        var now = DateTime.UtcNow;
        var key = baseName.Normalize(NormalizationForm.FormC);
        var state = Windows.GetOrAdd(key, static _ => new WindowState());
        var shouldLog = false;

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

        if (Interlocked.Increment(ref _callCount) % 256 == 0)
            Cleanup(now);

        return shouldLog;
    }

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
        if (!TryLog(fileName, out var suppressed))
            return;

        if (suppressed > 0)
        {
            Log.Warning(
                "Suppressed {SuppressedCount} additional gap-fill warnings for {FileName} in the previous 60 seconds.",
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
