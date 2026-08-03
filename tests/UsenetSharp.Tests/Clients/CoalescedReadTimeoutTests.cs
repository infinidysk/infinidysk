using UsenetSharp.Clients;
using UsenetSharpTest.Support;

namespace UsenetSharpTest.Protocol;

[TestFixture]
public class CoalescedReadTimeoutTests
{
    [Test]
    public void BeginIo_AdvancePastTimeout_CancelsToken()
    {
        var timeProvider = new ManualTimeProvider();
        using var timeout = new CoalescedReadTimeout(
            CancellationToken.None,
            TimeSpan.FromSeconds(1),
            timeProvider);

        timeout.BeginIo();
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Assert.That(timeout.Token.IsCancellationRequested, Is.True);
        Assert.That(timeout.IsTimeoutCancellation, Is.True);
    }

    [Test]
    public void DisposeDuringCancelCallback_DoesNotThrow()
    {
        var timeProvider = new ManualTimeProvider();
        CoalescedReadTimeout? timeout = null;
        timeout = new CoalescedReadTimeout(
            CancellationToken.None,
            TimeSpan.FromSeconds(1),
            timeProvider);

        using var registration = timeout.Token.Register(() =>
        {
            timeout.Dispose();
            timeout.Dispose();
        });

        timeout.BeginIo();

        Assert.DoesNotThrow(() => timeProvider.Advance(TimeSpan.FromSeconds(1)));
        Assert.That(timeout.Token.IsCancellationRequested, Is.True);
        Assert.That(timeout.IsTimeoutCancellation, Is.True);
    }

    [Test]
    public void DisposeThenAdvance_CallbackIsSafeNoOp()
    {
        var timeProvider = new ManualTimeProvider();
        var timeout = new CoalescedReadTimeout(
            CancellationToken.None,
            TimeSpan.FromSeconds(1),
            timeProvider);

        timeout.BeginIo();
        timeout.Dispose();

        Assert.DoesNotThrow(() => timeProvider.Advance(TimeSpan.FromSeconds(1)));
        Assert.That(timeout.Token.IsCancellationRequested, Is.False);
        Assert.That(timeout.IsTimeoutCancellation, Is.False);
    }
}
