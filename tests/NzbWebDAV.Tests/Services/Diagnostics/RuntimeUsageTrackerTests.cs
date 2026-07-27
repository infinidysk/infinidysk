using NzbWebDAV.Services.Diagnostics;

namespace NzbWebDAV.Tests.Services.Diagnostics;

public class RuntimeUsageTrackerTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Snapshot_BeforeTheFirstTick_ReportsNullsRatherThanDividingByZero()
    {
        var snapshot = new RuntimeUsageTracker(processorCount: 4).Snapshot();

        Assert.Equal(0, snapshot.SampleCount);
        Assert.Equal(0, snapshot.WindowSpanMs);
        Assert.Null(snapshot.LastSampleAtUtc);
        Assert.Null(snapshot.Cpu.CurrentPercent);
        Assert.Null(snapshot.Cpu.OneMinutePercent);
        Assert.Null(snapshot.Cpu.Peak);
        Assert.Null(snapshot.GcPause.CurrentPercent);
    }

    [Fact]
    public void Record_IgnoresSamplesWithNoMeasurableInterval()
    {
        var tracker = new RuntimeUsageTracker(processorCount: 4);

        // A clock that moved backwards, or two ticks landing on the same instant,
        // leaves nothing to divide by.
        Record(tracker, cpuMs: 1000, wallMs: 0);
        Record(tracker, cpuMs: 1000, wallMs: -5000);

        Assert.Equal(0, tracker.Snapshot().SampleCount);
    }

    [Fact]
    public void CpuPercent_IsAShareOfEveryCore()
    {
        var tracker = new RuntimeUsageTracker(processorCount: 4);

        // One core fully busy for the whole five seconds is a quarter of a four-core box.
        Record(tracker, cpuMs: 5000, wallMs: 5000);

        Assert.Equal(25, tracker.Snapshot().Cpu.CurrentPercent);
    }

    [Fact]
    public void GcPausePercent_IsAShareOfWallClockNotOfCoreTime()
    {
        var tracker = new RuntimeUsageTracker(processorCount: 4);

        // A pause stops the whole process, so the core count must not dilute it.
        Record(tracker, cpuMs: 0, wallMs: 5000, gcPauseMs: 500);

        Assert.Equal(10, tracker.Snapshot().GcPause.CurrentPercent);
    }

    [Fact]
    public void OneMinuteAverage_WeightsByWallClockInsteadOfAveragingPerTickPercentages()
    {
        var tracker = new RuntimeUsageTracker(processorCount: 4);

        Record(tracker, cpuMs: 4000, wallMs: 5000); // 20% of four cores
        Record(tracker, cpuMs: 4000, wallMs: 1000); // 100% of four cores

        var snapshot = tracker.Snapshot();

        // Averaging the two percentages would claim 60%. The long quiet sample has to
        // carry more weight than the short busy one: 8000ms over 6000ms of four cores.
        Assert.Equal(33.3, snapshot.Cpu.OneMinutePercent);
        Assert.Equal(100, snapshot.Cpu.CurrentPercent);
        Assert.Equal(6000, snapshot.WindowSpanMs);
        Assert.Equal(2, snapshot.SampleCount);
    }

    [Fact]
    public void Peak_OutlivesEvictionFromTheRollingWindow()
    {
        var tracker = new RuntimeUsageTracker(processorCount: 4);
        var busyAt = Origin;

        Record(tracker, cpuMs: 20000, wallMs: 5000, at: busyAt); // every core pegged
        for (var i = 1; i <= RuntimeUsageTracker.WindowSampleCount; i++)
            Record(tracker, cpuMs: 0, wallMs: 5000, at: Origin.AddSeconds(5 * i));

        var snapshot = tracker.Snapshot();

        // The window has rolled past the incident, which is exactly the case this
        // exists for: a pack collected minutes later must still show what happened.
        Assert.Equal(0, snapshot.Cpu.OneMinutePercent);
        Assert.Equal(0, snapshot.Cpu.CurrentPercent);
        Assert.NotNull(snapshot.Cpu.Peak);
        Assert.Equal(100, snapshot.Cpu.Peak.Percent);
        Assert.Equal(busyAt, snapshot.Cpu.Peak.AtUtc);
    }

    [Fact]
    public void PeakWhileReading_ReportsTheBusiestSampleThatHadALiveRead()
    {
        var tracker = new RuntimeUsageTracker(processorCount: 4);
        var startupAt = Origin;
        var playbackAt = Origin.AddMinutes(30);

        // Container startup with nothing playing, then a quieter moment during playback.
        Record(tracker, cpuMs: 16000, wallMs: 5000, at: startupAt, activeReads: 0);
        Record(tracker, cpuMs: 8000, wallMs: 5000, at: playbackAt, activeReads: 2);

        var cpu = tracker.Snapshot().Cpu;

        Assert.Equal(80, cpu.Peak!.Percent);
        Assert.Equal(startupAt, cpu.Peak.AtUtc);
        Assert.Equal(0, cpu.Peak.ActiveReads);

        // The unqualified peak is startup noise. Attribution is the whole point: the
        // figure that speaks to playback cost is the one taken with reads in flight.
        Assert.Equal(40, cpu.PeakWhileReading!.Percent);
        Assert.Equal(playbackAt, cpu.PeakWhileReading.AtUtc);
        Assert.Equal(2, cpu.PeakWhileReading.ActiveReads);
    }

    [Fact]
    public void PeakWhileReading_StaysNullUntilASampleCoincidesWithARead()
    {
        var tracker = new RuntimeUsageTracker(processorCount: 4);

        Record(tracker, cpuMs: 16000, wallMs: 5000, activeReads: 0);

        var snapshot = tracker.Snapshot();
        Assert.NotNull(snapshot.Cpu.Peak);
        Assert.Null(snapshot.Cpu.PeakWhileReading);
        Assert.Null(snapshot.GcPause.PeakWhileReading);
    }

    private static void Record(
        RuntimeUsageTracker tracker,
        double cpuMs,
        double wallMs,
        double gcPauseMs = 0,
        int activeReads = 0,
        DateTimeOffset? at = null) =>
        tracker.Record(
            TimeSpan.FromMilliseconds(cpuMs),
            TimeSpan.FromMilliseconds(gcPauseMs),
            TimeSpan.FromMilliseconds(wallMs),
            activeReads,
            at ?? Origin);
}
