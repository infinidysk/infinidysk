using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Tests.WebDav;

public class PropFindHandlerPatchTests
{
    [Fact]
    public async Task ForbiddenResponse_GetsFiniteDepthErrorBody()
    {
        var (context, body) = CreateContext(StatusCodes.Status403Forbidden);

        var written = await PropFindHandlerPatch.TryWriteFiniteDepthErrorAsync(context);

        Assert.True(written);
        Assert.Equal("application/xml; charset=utf-8", context.Response.ContentType);
        Assert.Equal(PropFindHandlerPatch.FiniteDepthErrorBody, Encoding.UTF8.GetString(body.ToArray()));
    }

    [Theory]
    [InlineData(StatusCodes.Status207MultiStatus)]
    [InlineData(StatusCodes.Status404NotFound)]
    public async Task NonForbiddenResponse_IsUnchanged(int statusCode)
    {
        var (context, body) = CreateContext(statusCode, contentType: "text/plain");
        await body.WriteAsync("existing"u8.ToArray());

        var written = await PropFindHandlerPatch.TryWriteFiniteDepthErrorAsync(context);

        Assert.False(written);
        Assert.Equal("text/plain", context.Response.ContentType);
        Assert.Equal("existing", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task StartedForbiddenResponse_IsUnchanged()
    {
        var (context, body) = CreateContext(
            StatusCodes.Status403Forbidden,
            hasStarted: true,
            contentType: "text/plain");
        await body.WriteAsync("existing"u8.ToArray());

        var written = await PropFindHandlerPatch.TryWriteFiniteDepthErrorAsync(context);

        Assert.False(written);
        Assert.Equal("text/plain", context.Response.ContentType);
        Assert.Equal("existing", Encoding.UTF8.GetString(body.ToArray()));
    }

    private static (DefaultHttpContext Context, MemoryStream Body) CreateContext(
        int statusCode,
        bool hasStarted = false,
        string? contentType = null)
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new TestHttpResponseFeature(hasStarted)
        {
            StatusCode = statusCode
        });
        context.Response.Body = body;
        context.Response.ContentType = contentType;
        return (context, body);
    }

    private sealed class TestHttpResponseFeature(bool hasStarted) : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; } = hasStarted;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
