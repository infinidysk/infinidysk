namespace NzbWebDAV.Services.Diagnostics;

/// <summary>
/// Rolling CPU and GC-pause figures for the support pack. Packs are almost always
/// collected after the symptom has passed, so an instantaneous sample taken at
/// pack-generation time describes an idle process and answers the wrong question.
/// This keeps a short window for "recently" plus process-lifetime peaks, which do
/// span the period under investigation.
///
/// A peak is also tracked separately for samples taken while a read was in flight.
/// An unqualified peak is usually container startup (JIT, migrations, the first
/// scan), so the read-attributed peak is the one that says whether playback itself
/// is what loads the cores.
///
/// Pure accumulator: <see cref="RuntimeUsageSampler"/> owns the timer and the
/// counter reads.
/// </summary>
public sealed class RuntimeUsageTracker(int? processorCount = null)
{
    /// <summary>Retained samples. Twelve at the sampler's five-second tick is one minute.</summary>
    public const int WindowSampleCount = 12;

    private readonly int _cores = Math.Max(1, processorCount ?? Environment.ProcessorCount);
    private readonly Lock _gate = new();
    private readonly Sample[] _window = new Sample[WindowSampleCount];

    private int _windowNext;
    private int _windowCount;
    private long _sampleCount;
    private DateTimeOffset? _lastSampleAt;
    private RuntimeUsagePeak? _cpuPeak;
    private RuntimeUsagePeak? _cpuPeakWhileReading;
    private RuntimeUsagePeak? _gcPeak;
    private RuntimeUsagePeak? _gcPeakWhileReading;

    public void Record(
        TimeSpan cpuDelta,
        TimeSpan gcPauseDelta,
        TimeSpan elapsed,
        int activeReads,
        DateTimeOffset at)
    {
        // A non-positive window means the clock moved backwards or two ticks
        // landed on the same instant; there is nothing to divide by.
        if (elapsed <= TimeSpan.Zero) return;

        var wallMs = elapsed.TotalMilliseconds;
        var cpuMs = Math.Max(0, cpuDelta.TotalMilliseconds);
        var gcPauseMs = Math.Max(0, gcPauseDelta.TotalMilliseconds);
        var reads = Math.Max(0, activeReads);

        // CPU is a share of the whole machine, so 100 means every core busy. A GC
        // pause stops the process regardless of core count, so it is a share of
        // wall clock only.
        var cpuPercent = Round(cpuMs / (wallMs * _cores) * 100);
        var gcPercent = Round(gcPauseMs / wallMs * 100);

        lock (_gate)
        {
            _window[_windowNext] = new Sample(cpuMs, gcPauseMs, wallMs);
            _windowNext = (_windowNext + 1) % WindowSampleCount;
            if (_windowCount < WindowSampleCount) _windowCount++;
            _sampleCount++;
            _lastSampleAt = at;

            _cpuPeak = Higher(_cpuPeak, cpuPercent, at, reads);
            _gcPeak = Higher(_gcPeak, gcPercent, at, reads);
            if (reads > 0)
            {
                _cpuPeakWhileReading = Higher(_cpuPeakWhileReading, cpuPercent, at, reads);
                _gcPeakWhileReading = Higher(_gcPeakWhileReading, gcPercent, at, reads);
            }
        }
    }

    public RuntimeUsageSnapshot Snapshot()
    {
        lock (_gate)
        {
            if (_windowCount == 0)
            {
                return new RuntimeUsageSnapshot(
                    _cores,
                    SampleCount: 0,
                    WindowSpanMs: 0,
                    LastSampleAtUtc: null,
                    Cpu: new RuntimeUsageMetric(null, null, null, null),
                    GcPause: new RuntimeUsageMetric(null, null, null, null));
            }

            // Newest first, so index 0 is the most recent tick.
            var newest = _window[(_windowNext - 1 + WindowSampleCount) % WindowSampleCount];
            double cpuMs = 0, gcPauseMs = 0, wallMs = 0;
            for (var i = 0; i < _windowCount; i++)
            {
                var sample = _window[(_windowNext - 1 - i + 2 * WindowSampleCount) % WindowSampleCount];
                cpuMs += sample.CpuMs;
                gcPauseMs += sample.GcPauseMs;
                wallMs += sample.WallMs;
            }

            return new RuntimeUsageSnapshot(
                _cores,
                _sampleCount,
                (long)wallMs,
                _lastSampleAt,
                new RuntimeUsageMetric(
                    Round(newest.CpuMs / (newest.WallMs * _cores) * 100),
                    Round(cpuMs / (wallMs * _cores) * 100),
                    _cpuPeak,
                    _cpuPeakWhileReading),
                new RuntimeUsageMetric(
                    Round(newest.GcPauseMs / newest.WallMs * 100),
                    Round(gcPauseMs / wallMs * 100),
                    _gcPeak,
                    _gcPeakWhileReading));
        }
    }

    private static RuntimeUsagePeak Higher(
        RuntimeUsagePeak? current,
        double percent,
        DateTimeOffset at,
        int activeReads) =>
        current is null || percent > current.Percent
            ? new RuntimeUsagePeak(percent, at, activeReads)
            : current;

    private static double Round(double percent) => Math.Round(percent, 1);

    private readonly record struct Sample(double CpuMs, double GcPauseMs, double WallMs);
}

/// <param name="SampleCount">
/// Samples recorded since startup. Zero means the sampler has not ticked yet, which
/// is the only case where the figures below are null.
/// </param>
/// <param name="WindowSpanMs">
/// Wall clock covered by the retained window. Under 60_000 means the one-minute
/// averages are over a partial window and should be read as such.
/// </param>
/// <param name="LastSampleAtUtc">
/// When the sampler last ticked. Far behind the pack's generation time means the
/// sampler is wedged and the rolling figures are stale.
/// </param>
public sealed record RuntimeUsageSnapshot(
    int ProcessorCount,
    long SampleCount,
    long WindowSpanMs,
    DateTimeOffset? LastSampleAtUtc,
    RuntimeUsageMetric Cpu,
    RuntimeUsageMetric GcPause);

public sealed record RuntimeUsageMetric(
    double? CurrentPercent,
    double? OneMinutePercent,
    RuntimeUsagePeak? Peak,
    RuntimeUsagePeak? PeakWhileReading);

public sealed record RuntimeUsagePeak(double Percent, DateTimeOffset AtUtc, int ActiveReads);
