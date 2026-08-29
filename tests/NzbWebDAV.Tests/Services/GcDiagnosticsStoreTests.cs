using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Services;

public sealed class GcDiagnosticsStoreTests
{
    [Fact]
    public void TryBegin_FirstAdmissionStarts()
    {
        using var store = new GcDiagnosticsStore(new ControllableTimeProvider());
        var result = store.TryBegin();
        Assert.Equal(GcDiagnosticsAdmission.Started, result.Status);
        Assert.Null(result.RetryAfterSeconds);
        store.End();
    }

    [Fact]
    public void TryBegin_RejectsConcurrentAdmission()
    {
        using var store = new GcDiagnosticsStore(new ControllableTimeProvider());
        Assert.Equal(GcDiagnosticsAdmission.Started, store.TryBegin().Status);
        var rejected = store.TryBegin();
        Assert.Equal(GcDiagnosticsAdmission.AlreadyRunning, rejected.Status);
        Assert.Equal((int)GcDiagnosticsStore.Cooldown.TotalSeconds, rejected.RetryAfterSeconds);
        store.End();
    }

    [Fact]
    public void TryBegin_ConcurrentCallers_AdmitExactlyOnce()
    {
        using var store = new GcDiagnosticsStore(new ControllableTimeProvider());
        var results = new GcDiagnosticsAdmission[2];
        BarrierThreads.Run(2, i => results[i] = store.TryBegin().Status);

        Assert.Equal(1, results.Count(status => status == GcDiagnosticsAdmission.Started));
        Assert.Equal(1, results.Count(status => status == GcDiagnosticsAdmission.AlreadyRunning));
        store.End();
    }

    [Fact]
    public void TryBegin_RejectsCooldownThenAdmitsAtExactBoundary()
    {
        var clock = new ControllableTimeProvider();
        using var store = new GcDiagnosticsStore(clock);
        Assert.Equal(GcDiagnosticsAdmission.Started, store.TryBegin().Status);
        store.End();

        clock.Advance(GcDiagnosticsStore.Cooldown - TimeSpan.FromTicks(1));
        var cooldown = store.TryBegin();
        Assert.Equal(GcDiagnosticsAdmission.Cooldown, cooldown.Status);
        Assert.Equal(1, cooldown.RetryAfterSeconds);

        clock.Advance(TimeSpan.FromTicks(1));
        var admitted = store.TryBegin();
        Assert.Equal(GcDiagnosticsAdmission.Started, admitted.Status);
        Assert.Null(admitted.RetryAfterSeconds);
        store.End();
    }

    [Fact]
    public void TryBegin_UsesMonotonicTimestampNotWallClock()
    {
        var clock = new SplitTimeProvider();
        using var store = new GcDiagnosticsStore(clock);
        Assert.Equal(GcDiagnosticsAdmission.Started, store.TryBegin().Status);
        store.End();

        clock.UtcNow -= TimeSpan.FromHours(1);
        var cooldown = store.TryBegin();
        Assert.Equal(GcDiagnosticsAdmission.Cooldown, cooldown.Status);

        clock.Timestamp += (long)(GcDiagnosticsStore.Cooldown.TotalSeconds * clock.TimestampFrequency);
        var admitted = store.TryBegin();
        Assert.Equal(GcDiagnosticsAdmission.Started, admitted.Status);
        store.End();
    }

    [Fact]
    public void TryBegin_StartsCooldownEvenWhenAttemptFailsToFinish()
    {
        var clock = new ControllableTimeProvider();
        using var store = new GcDiagnosticsStore(clock);
        Assert.Equal(GcDiagnosticsAdmission.Started, store.TryBegin().Status);
        store.End();

        var cooldown = store.TryBegin();
        Assert.Equal(GcDiagnosticsAdmission.Cooldown, cooldown.Status);
        Assert.NotNull(cooldown.RetryAfterSeconds);
    }

    private sealed class SplitTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        public long Timestamp { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long GetTimestamp() => Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
