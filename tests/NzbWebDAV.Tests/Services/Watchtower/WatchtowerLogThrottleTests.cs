using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services.Watchtower;

public class WatchtowerLogThrottleTests
{
    [Fact]
    public void ShouldLog_AllowsFirstCallThenSuppressesWithinTheInterval()
    {
        var throttle = new WatchtowerLogThrottle();
        var interval = TimeSpan.FromMinutes(15);

        Assert.True(throttle.ShouldLog("cycle", interval, out var firstSuppressed));
        Assert.Equal(0, firstSuppressed);

        for (var i = 0; i < 45; i++)
            Assert.False(throttle.ShouldLog("cycle", interval, out _));
    }

    [Fact]
    public void ShouldLog_ReportsSuppressedCountOnceTheIntervalElapses()
    {
        var throttle = new WatchtowerLogThrottle();

        Assert.True(throttle.ShouldLog("cycle", TimeSpan.Zero, out _));
        Assert.False(throttle.ShouldLog("cycle", TimeSpan.FromMinutes(15), out _));
        Assert.False(throttle.ShouldLog("cycle", TimeSpan.FromMinutes(15), out _));

        // A zero interval always elapses, standing in for the wall-clock wait.
        Assert.True(throttle.ShouldLog("cycle", TimeSpan.Zero, out var suppressed));
        Assert.Equal(2, suppressed);

        // The count resets once reported.
        Assert.True(throttle.ShouldLog("cycle", TimeSpan.Zero, out var afterReport));
        Assert.Equal(0, afterReport);
    }

    [Fact]
    public void ShouldLog_TracksKeysIndependently()
    {
        var throttle = new WatchtowerLogThrottle();
        var interval = TimeSpan.FromMinutes(60);

        Assert.True(throttle.ShouldLog("cycle", interval, out _));
        Assert.True(throttle.ShouldLog("no-profile", interval, out _));
        Assert.False(throttle.ShouldLog("cycle", interval, out _));
        Assert.False(throttle.ShouldLog("no-profile", interval, out _));
    }

    [Fact]
    public void Reset_LetsTheNextCallLogImmediately()
    {
        var throttle = new WatchtowerLogThrottle();
        var interval = TimeSpan.FromMinutes(60);

        Assert.True(throttle.ShouldLog("no-profile", interval, out _));
        Assert.False(throttle.ShouldLog("no-profile", interval, out _));

        throttle.Reset("no-profile");

        Assert.True(throttle.ShouldLog("no-profile", interval, out var suppressed));
        Assert.Equal(0, suppressed);
    }
}
