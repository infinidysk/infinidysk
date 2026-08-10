using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Config;

namespace NzbWebDAV.Tests.Api;

public class GetQueueRequestTests
{
    [Fact]
    public void OmittedLimitDefaultsToUnlimited()
    {
        var context = new DefaultHttpContext();
        var request = new GetQueueRequest(context, new ConfigManager());

        Assert.Equal(int.MaxValue, request.Limit);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(0, int.MaxValue)]
    [InlineData(-5, int.MaxValue)]
    public void ParsesLimitParameter(int limitParam, int expectedLimit)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?limit={limitParam}");

        var request = new GetQueueRequest(context, new ConfigManager());

        Assert.Equal(expectedLimit, request.Limit);
    }

    [Fact]
    public void RejectsNonIntegerLimit()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?limit=abc");

        var ex = Assert.Throws<BadHttpRequestException>(() => new GetQueueRequest(context, new ConfigManager()));
        Assert.Equal("Invalid limit parameter", ex.Message);
    }
}
