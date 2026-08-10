using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace NzbWebDAV.Services.Diagnostics;

/// <summary>
/// Feeds <see cref="RuntimeUsageTracker"/> from the process CPU and GC-pause
/// counters on a fixed tick, tagging each sample with the number of reads in
/// flight so a peak can be attributed to playback rather than to a queue import
/// or a health sweep. One timer and a few counter reads; nothing touches a hot path.
/// </summary>
public sealed class RuntimeUsageSampler(
    RuntimeUsageTracker tracker,
    ActiveReadRegistry activeReadRegistry) : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var previous = TryReadCounters();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            var current = TryReadCounters();

            // A failed read on either side leaves no interval to measure. Re-baseline
            // rather than treating the gap as one sample, which would otherwise bill
            // every second since the last good read into a five-second bucket and
            // invent an enormous peak.
            if (previous is { } before && current is { } after)
            {
                try
                {
                    tracker.Record(
                        after.Cpu - before.Cpu,
                        after.GcPause - before.GcPause,
                        Stopwatch.GetElapsedTime(before.Timestamp, after.Timestamp),
                        activeReadRegistry.Snapshot().Count,
                        DateTimeOffset.UtcNow);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Log.Debug(e, "Runtime usage sampler could not record a sample");
                }
            }

            previous = current;
        }
    }

    private static Counters? TryReadCounters()
    {
        try
        {
            // Read CPU first: it is the one that can throw, and taking the timestamp
            // after it keeps the measured interval aligned with the CPU delta.
            var cpu = Environment.CpuUsage.TotalTime;
            return new Counters(cpu, GC.GetTotalPauseDuration(), Stopwatch.GetTimestamp());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Debug only: a counter that is unavailable on this platform would
            // otherwise dump a stack into the operator's log every five seconds.
            Log.Debug(e, "Runtime usage sampler could not read the process CPU and GC counters");
            return null;
        }
    }

    private readonly record struct Counters(TimeSpan Cpu, TimeSpan GcPause, long Timestamp);
}
