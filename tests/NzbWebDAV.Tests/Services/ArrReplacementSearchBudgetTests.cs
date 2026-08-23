using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ArrReplacementSearchBudgetTests
{
    [Fact]
    public void TryReserve_BoundsOneMediaItemWithinWindow()
    {
        var time = new TestTimeProvider();
        var budget = new ArrReplacementSearchBudget(time);

        Assert.True(budget.TryReserve("http://radarr|movie:42", limit: 3, TimeSpan.FromMinutes(30)));
        Assert.True(budget.TryReserve("http://radarr|movie:42", limit: 3, TimeSpan.FromMinutes(30)));
        Assert.True(budget.TryReserve("http://radarr|movie:42", limit: 3, TimeSpan.FromMinutes(30)));
        Assert.False(budget.TryReserve("http://radarr|movie:42", limit: 3, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void TryReserve_KeepsHostsAndMediaItemsIndependent()
    {
        var budget = new ArrReplacementSearchBudget(new TestTimeProvider());

        Assert.True(budget.TryReserve("http://radarr-one|movie:42", limit: 1, TimeSpan.FromMinutes(30)));
        Assert.True(budget.TryReserve("http://radarr-two|movie:42", limit: 1, TimeSpan.FromMinutes(30)));
        Assert.True(budget.TryReserve("http://radarr-one|movie:43", limit: 1, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void TryReserve_FailsClosedForNewKeysAtCapacity()
    {
        var budget = new ArrReplacementSearchBudget(new TestTimeProvider());
        for (var i = 0; i < 4096; i++)
            Assert.True(budget.TryReserve($"http://radarr|movie:{i}", limit: 2, TimeSpan.FromMinutes(30)));

        // A brand-new key must not evict an active reservation; it is denied instead.
        Assert.False(budget.TryReserve("http://radarr|movie:99999", limit: 2, TimeSpan.FromMinutes(30)));

        // Already-tracked keys keep working at capacity.
        Assert.True(budget.TryReserve("http://radarr|movie:0", limit: 2, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void ReleaseLastReservation_RefundsBudget()
    {
        var budget = new ArrReplacementSearchBudget(new TestTimeProvider());

        Assert.True(budget.TryReserve("http://radarr|movie:42", limit: 1, TimeSpan.FromMinutes(30)));
        budget.ReleaseLastReservation("http://radarr|movie:42");

        Assert.True(budget.TryReserve("http://radarr|movie:42", limit: 1, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void TryReserve_ExpiresOldReservations()
    {
        var time = new TestTimeProvider();
        var budget = new ArrReplacementSearchBudget(time);

        Assert.True(budget.TryReserve("http://radarr|movie:42", limit: 1, TimeSpan.FromMinutes(30)));
        time.Advance(TimeSpan.FromMinutes(30).Add(TimeSpan.FromTicks(1)));

        Assert.True(budget.TryReserve("http://radarr|movie:42", limit: 1, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void ApplyReplacementSearchBudget_DowngradesAlternateReleasesForOneMovie()
    {
        var budget = new ArrReplacementSearchBudget(new TestTimeProvider());
        var config = new ArrConfig
        {
            QueueReplacementSearchLimit = 1,
            QueueReplacementSearchWindowMinutes = 30,
        };

        var firstReleaseAction = ArrMonitoringService.ApplyReplacementSearchBudget(
            ArrConfig.QueueAction.RemoveAndBlocklistAndSearch,
            "http://radarr|movie:42",
            config,
            budget);
        var alternateReleaseAction = ArrMonitoringService.ApplyReplacementSearchBudget(
            ArrConfig.QueueAction.RemoveAndBlocklistAndSearch,
            "http://radarr|movie:42",
            config,
            budget);

        Assert.Equal(ArrConfig.QueueAction.RemoveAndBlocklistAndSearch, firstReleaseAction);
        Assert.Equal(ArrConfig.QueueAction.RemoveAndBlocklist, alternateReleaseAction);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
