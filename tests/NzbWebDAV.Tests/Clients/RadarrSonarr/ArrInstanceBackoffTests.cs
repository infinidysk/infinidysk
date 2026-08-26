using System.Net;
using System.Net.Sockets;
using NzbWebDAV.Clients.RadarrSonarr;

namespace NzbWebDAV.Tests.Clients.RadarrSonarr;

public sealed class ArrInstanceBackoffTests
{
    private static HttpRequestException Refused() =>
        new("Connection refused", new SocketException((int)SocketError.ConnectionRefused));

    private static HttpRequestException Http500() =>
        new("server error", null, HttpStatusCode.InternalServerError);

    [Fact]
    public void SingleFailure_DoesNotBackOff()
    {
        var backoff = new ArrInstanceBackoff();
        backoff.RecordFailure("http://sonarr:8989", Refused());

        Assert.False(backoff.IsInBackoff("http://sonarr:8989"));
    }

    [Fact]
    public void TwoConsecutiveReachabilityFailures_EnterBackoff()
    {
        var backoff = new ArrInstanceBackoff();
        backoff.RecordFailure("http://sonarr:8989", Refused());
        backoff.RecordFailure("http://sonarr:8989", Refused());

        Assert.True(backoff.IsInBackoff("http://sonarr:8989"));
        Assert.True(backoff.GetRemainingBackoff("http://sonarr:8989") > TimeSpan.Zero);
    }

    [Fact]
    public void Success_ResetsBackoff()
    {
        var backoff = new ArrInstanceBackoff();
        backoff.RecordFailure("http://sonarr:8989", Refused());
        backoff.RecordFailure("http://sonarr:8989", Refused());
        Assert.True(backoff.IsInBackoff("http://sonarr:8989"));

        backoff.RecordSuccess("http://sonarr:8989");

        Assert.False(backoff.IsInBackoff("http://sonarr:8989"));
    }

    [Fact]
    public void HttpErrorStatus_DoesNotCountAsReachabilityFailure()
    {
        var backoff = new ArrInstanceBackoff();
        // A live host answering with 500 is not "down"; it must not back off.
        backoff.RecordFailure("http://sonarr:8989", Http500());
        backoff.RecordFailure("http://sonarr:8989", Http500());
        backoff.RecordFailure("http://sonarr:8989", Http500());

        Assert.False(backoff.IsInBackoff("http://sonarr:8989"));
    }

    [Fact]
    public void Timeout_CountsAsReachabilityFailure()
    {
        var backoff = new ArrInstanceBackoff();
        backoff.RecordFailure("http://sonarr:8989", new TaskCanceledException("timed out"));
        backoff.RecordFailure("http://sonarr:8989", new TaskCanceledException("timed out"));

        Assert.True(backoff.IsInBackoff("http://sonarr:8989"));
    }

    [Fact]
    public void HostsAreIndependent()
    {
        var backoff = new ArrInstanceBackoff();
        backoff.RecordFailure("http://sonarr:8989", Refused());
        backoff.RecordFailure("http://sonarr:8989", Refused());

        Assert.True(backoff.IsInBackoff("http://sonarr:8989"));
        Assert.False(backoff.IsInBackoff("http://radarr:7878"));
    }

    [Fact]
    public void HostKey_IgnoresCaseAndTrailingSlash()
    {
        var backoff = new ArrInstanceBackoff();
        backoff.RecordFailure("http://Sonarr:8989/", Refused());
        backoff.RecordFailure("http://sonarr:8989", Refused());

        Assert.True(backoff.IsInBackoff("HTTP://SONARR:8989/"));
    }
}
