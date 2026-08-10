using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using UsenetSharp.Exceptions;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class UsenetConnectionLimitDetectorTests
{
    [Fact]
    public void AuthStage_502WithConnectionLimit_ReturnsLearned()
    {
        var ex = new CouldNotLoginToUsenetException(
            "Could not login to usenet host: 502 connection limit (150) reached",
            responseCode: 502);

        Assert.True(UsenetConnectionLimitDetector.TryLearn(ex, out var learned));
        Assert.Equal(150, learned);
    }

    [Fact]
    public void AuthStage_502WithoutConnectionLimit_ReturnsFalse()
    {
        var ex = new CouldNotLoginToUsenetException(
            "Could not login to usenet host: 502 authentication failed",
            responseCode: 502);

        Assert.False(UsenetConnectionLimitDetector.TryLearn(ex, out _));
    }

    [Fact]
    public void AuthStage_481WithConnectionLimitMessage_ReturnsFalse()
    {
        // Wrong response code — only 502 triggers the shrink.
        var ex = new CouldNotLoginToUsenetException(
            "Could not login to usenet host: 481 connection limit (50) reached",
            responseCode: 481);

        Assert.False(UsenetConnectionLimitDetector.TryLearn(ex, out _));
    }

    [Fact]
    public void AuthStage_NoResponseCode_ReturnsFalse()
    {
        var ex = new CouldNotLoginToUsenetException(
            "Could not login to usenet host: 502 connection limit (100) reached");

        Assert.False(UsenetConnectionLimitDetector.TryLearn(ex, out _));
    }

    [Fact]
    public void GreetingStage_UsenetConnectionException502_ReturnsLearned()
    {
        var inner = new UsenetConnectionException("502 connection limit (75) reached")
        {
            ResponseCode = 502,
        };
        var ex = new CouldNotConnectToUsenetException("Could not connect.", inner);

        Assert.True(UsenetConnectionLimitDetector.TryLearn(ex, out var learned));
        Assert.Equal(75, learned);
    }

    [Fact]
    public void GreetingStage_UsenetConnectionException400_ReturnsFalse()
    {
        var inner = new UsenetConnectionException("400 connection limit (50) reached")
        {
            ResponseCode = 400,
        };
        var ex = new CouldNotConnectToUsenetException("Could not connect.", inner);

        Assert.False(UsenetConnectionLimitDetector.TryLearn(ex, out _));
    }

    [Fact]
    public void DifferentLimit_ParsesCorrectly()
    {
        var ex = new CouldNotLoginToUsenetException(
            "Could not login to usenet host: 502 connection limit (30) reached",
            responseCode: 502);

        Assert.True(UsenetConnectionLimitDetector.TryLearn(ex, out var learned));
        Assert.Equal(30, learned);
    }

    [Fact]
    public void CaseInsensitive_Matches()
    {
        var ex = new CouldNotLoginToUsenetException(
            "Could not login to usenet host: 502 Connection Limit (100) Reached",
            responseCode: 502);

        Assert.True(UsenetConnectionLimitDetector.TryLearn(ex, out var learned));
        Assert.Equal(100, learned);
    }

    [Fact]
    public void DeepChain_FindsNested502()
    {
        var inner = new CouldNotLoginToUsenetException(
            "Could not login to usenet host: 502 connection limit (60) reached",
            responseCode: 502);
        var ex = new InvalidOperationException("wrapper", new Exception("middle", inner));

        Assert.True(UsenetConnectionLimitDetector.TryLearn(ex, out var learned));
        Assert.Equal(60, learned);
    }

    [Fact]
    public void UnrelatedException_ReturnsFalse()
    {
        var ex = new IOException("network error");

        Assert.False(UsenetConnectionLimitDetector.TryLearn(ex, out _));
    }

    [Fact]
    public void ZeroLimit_ReturnsFalse()
    {
        var ex = new CouldNotLoginToUsenetException(
            "Could not login to usenet host: 502 connection limit (0) reached",
            responseCode: 502);

        Assert.False(UsenetConnectionLimitDetector.TryLearn(ex, out _));
    }
}
