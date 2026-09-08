using NzbWebDAV.Api.Controllers.RcloneMounts;

namespace NzbWebDAV.Tests.Api;

/// <summary>
/// The status endpoint makes three RC calls in sequence, each carrying its own
/// 30-second transport timeout. They share one deadline so a wedged daemon
/// cannot hold the endpoint for the sum of them while the settings tab polls it
/// every ten seconds.
/// </summary>
public class RcloneMountsStatusDeadlineTests
{
    [Fact]
    public void StatusBudget_IsShorterThanTheTabsPollInterval()
    {
        // Requests would otherwise queue up behind one another.
        Assert.True(
            GetRcloneMountsStatusController.StatusBudget < TimeSpan.FromSeconds(10),
            "the status budget must finish before the settings tab polls again");
    }

    [Fact]
    public async Task CreateDeadline_CancelsWhenTheCallerDisconnects()
    {
        using var requestAborted = new CancellationTokenSource();
        using var deadline = GetRcloneMountsStatusController.CreateDeadline(requestAborted.Token);

        Assert.False(deadline.Token.IsCancellationRequested);

        await requestAborted.CancelAsync();

        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task CreateDeadline_CancelsOnItsOwnBudget_EvenWhileTheRequestIsAlive()
    {
        using var requestAborted = new CancellationTokenSource();
        using var deadline = GetRcloneMountsStatusController.CreateDeadline(requestAborted.Token);

        // Bring the deadline forward rather than waiting out the real budget.
        deadline.CancelAfter(TimeSpan.FromMilliseconds(20));
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.False(requestAborted.IsCancellationRequested);
    }
}
