using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.SwitchQueue;

namespace NzbWebDAV.Tests.Api;

public class SwitchQueueRequestTests
{
    [Fact]
    public void New_ParsesPeerTarget()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?value={source}&value2={target}");

        var request = SwitchQueueRequest.New(context);

        Assert.Equal(source, request.SourceId);
        Assert.Equal(target.ToString(), request.Target);
    }

    [Theory]
    [InlineData("?value2=0")]
    [InlineData("?value=not-a-guid&value2=0")]
    [InlineData("?value=00000000-0000-0000-0000-000000000000")]
    public void New_RequiresSourceAndTarget(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);

        Assert.Throws<BadHttpRequestException>(() => SwitchQueueRequest.New(context));
    }
}
