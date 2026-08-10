using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers;

namespace NzbWebDAV.Tests.Api;

public class SabNzoIdsParsingTests
{
    [Fact]
    public void ParseQuery_SplitsCommaSeparatedAndRepeatedValues()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var context = CreateContext($"?value={first},{second}&value={third}");

        var ids = SabNzoIdsParser.ParseQuery(context);

        Assert.Equal([first, second, third], ids);
    }

    [Fact]
    public async Task ParseAsync_MergesQueryAndJsonBody()
    {
        var fromQuery = Guid.NewGuid();
        var fromBody = Guid.NewGuid();
        var context = CreateContext($"?value={fromQuery}");
        var json = JsonSerializer.Serialize(new { nzo_ids = new[] { fromBody } });
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Request.Body = body;
        context.Request.ContentType = "application/json";

        var result = await SabNzoIdsParser.ParseAsync(context, CancellationToken.None);

        Assert.Equal([fromQuery, fromBody], result.NzoIds);
    }

    private static DefaultHttpContext CreateContext(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString);
        context.Request.Body = Stream.Null;
        return context;
    }
}
