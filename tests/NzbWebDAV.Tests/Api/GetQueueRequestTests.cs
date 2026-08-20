using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
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

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-100, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    public void ClampsNegativeStartToZero(int startParam, int expectedStart)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?start={startParam}");

        var request = new GetQueueRequest(context, new ConfigManager());

        Assert.Equal(expectedStart, request.Start);
    }

    [Fact]
    public void RejectsNonIntegerLimit()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?limit=abc");

        var ex = Assert.Throws<ApiValidationException>(() => new GetQueueRequest(context, new ConfigManager()));
        Assert.Equal("Invalid limit parameter", ex.Message);
    }

    [Fact]
    public void ParsesListFiltersAndDisplaySort()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?search=The%20Show&status=paused&sort=name&dir=asc");

        var request = new GetQueueRequest(context, new ConfigManager());

        Assert.Equal("The Show", request.Search);
        Assert.Equal("Paused", request.Status);
        Assert.Equal("name", request.Sort);
        Assert.Equal("asc", request.Direction);
    }
}
