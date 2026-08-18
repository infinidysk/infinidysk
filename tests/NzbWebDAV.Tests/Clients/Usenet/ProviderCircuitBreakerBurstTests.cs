using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ProviderCircuitBreakerBurstTests
{
    [Fact]
    public void FailuresWithinOneBurst_DoNotTripTheBreaker()
    {
        var clock = new TestClock();
        var breaker = CreateBreaker(clock);

        breaker.RecordFailure();
        clock.Advance(milliseconds: 500);
        breaker.RecordFailure();
        clock.Advance(milliseconds: 500);
        breaker.RecordFailure();

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(ProviderCircuitState.Closed, snapshot.State);
        Assert.Equal(3, snapshot.FailureCount);
    }

    [Fact]
    public void FailuresAcrossThreeBursts_TripTheBreaker()
    {
        var clock = new TestClock();
        var breaker = CreateBreaker(clock);

        breaker.RecordFailure();
        clock.Advance(milliseconds: 2_000);
        breaker.RecordFailure();
        clock.Advance(milliseconds: 2_000);
        breaker.RecordFailure();

        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
    }

    [Fact]
    public void ContinuousFailures_StillTripTheBreaker()
    {
        var clock = new TestClock();
        var breaker = CreateBreaker(clock);

        for (var attempt = 0; attempt < 12 && !breaker.IsTripped; attempt++)
        {
            breaker.RecordFailure();
            clock.Advance(milliseconds: 500);
        }

        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot().State);
        Assert.InRange(clock.Now, 4_000, 6_000);
    }

    [Fact]
    public void CapCooldown_ShortensOpenCooldownWithoutResettingTheLadder()
    {
        var clock = new TestClock();
        var breaker = CreateBreaker(clock);
        breaker.RecordFailure();
        clock.Advance(milliseconds: 2_000);
        breaker.RecordFailure();
        clock.Advance(milliseconds: 2_000);
        breaker.RecordFailure();
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);

        breaker.CapCooldown(TimeSpan.FromSeconds(10));

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
        Assert.InRange(snapshot.CooldownRemainingSeconds ?? 0, 9, 10);
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);
    }

    [Fact]
    public void CapCooldown_DoesNotExtendOrOpenTheCircuit()
    {
        var clock = new TestClock();
        var breaker = CreateBreaker(clock);

        breaker.CapCooldown(TimeSpan.FromSeconds(10));

        Assert.Equal(ProviderCircuitState.Closed, breaker.GetSnapshot().State);
    }

    private static ProviderCircuitBreaker CreateBreaker(TestClock clock) =>
        new("burst-test", coalesceFailureBursts: true) { Clock = () => clock.Now };

    private sealed class TestClock
    {
        public long Now { get; private set; }

        public void Advance(long milliseconds) => Now += milliseconds;
    }
}
