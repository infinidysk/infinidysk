using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ProviderCircuitBreakerCooldownTests
{
    [Fact]
    public void ConfiguredInitialCooldown_AppliesToTheFirstTrip()
    {
        var clock = new TestClock();
        var breaker = CreateBreaker(clock, initial: TimeSpan.FromSeconds(15));

        breaker.RecordConnectionFailure();

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
        Assert.InRange(snapshot.CooldownRemainingSeconds ?? 0, 14, 15);
    }

    [Fact]
    public void LadderDoublesFromTheConfiguredInitialCooldown()
    {
        var clock = new TestClock();
        var breaker = CreateBreaker(clock, initial: TimeSpan.FromSeconds(15));

        breaker.RecordConnectionFailure();

        Assert.Equal(TimeSpan.FromSeconds(30), breaker.CurrentCooldown);
    }

    [Fact]
    public void LadderStopsAtTheConfiguredCeiling()
    {
        var clock = new TestClock();
        var breaker = CreateBreaker(
            clock,
            initial: TimeSpan.FromSeconds(15),
            max: TimeSpan.FromSeconds(45));

        for (var trip = 0; trip < 5; trip++)
        {
            breaker.RecordConnectionFailure();
            clock.Advance(milliseconds: 60_000);
        }

        Assert.Equal(TimeSpan.FromSeconds(45), breaker.CurrentCooldown);
    }

    [Fact]
    public void BodySuccess_ResetsTheLadderToTheConfiguredInitialCooldown()
    {
        var clock = new TestClock();
        var breaker = CreateBreaker(clock, initial: TimeSpan.FromSeconds(15));

        breaker.RecordConnectionFailure();
        clock.Advance(milliseconds: 15_000);
        breaker.RecordSuccess();

        Assert.Equal(TimeSpan.FromSeconds(15), breaker.CurrentCooldown);
    }

    [Fact]
    public void CeilingBelowTheInitialCooldown_IsRaisedToIt()
    {
        var clock = new TestClock();
        var breaker = CreateBreaker(
            clock,
            initial: TimeSpan.FromSeconds(120),
            max: TimeSpan.FromSeconds(30));

        breaker.RecordConnectionFailure();

        var snapshot = breaker.GetSnapshot();
        Assert.InRange(snapshot.CooldownRemainingSeconds ?? 0, 119, 120);
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);
    }

    [Fact]
    public void UnconfiguredBreaker_KeepsTheShippedLadder()
    {
        var clock = new TestClock();
        var breaker = new ProviderCircuitBreaker("cooldown-test") { Clock = () => clock.Now };

        breaker.RecordConnectionFailure();

        var snapshot = breaker.GetSnapshot();
        Assert.InRange(snapshot.CooldownRemainingSeconds ?? 0, 59, 60);
        Assert.Equal(TimeSpan.FromSeconds(120), breaker.CurrentCooldown);
    }

    private static ProviderCircuitBreaker CreateBreaker(
        TestClock clock,
        TimeSpan? initial = null,
        TimeSpan? max = null) =>
        new("cooldown-test", initialCooldown: initial, maxCooldown: max) { Clock = () => clock.Now };

    private sealed class TestClock
    {
        public long Now { get; private set; }

        public void Advance(long milliseconds) => Now += milliseconds;
    }
}
