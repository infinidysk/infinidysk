using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NWebDav.Server;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Tests.WebDav;

public class PropFindHandlerPatchTests
{
    private static readonly XNamespace Dav = WebDavNamespaces.DavNs;

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

    [Theory]
    [InlineData(StatusCodes.Status207MultiStatus, "application/xml; charset=utf-8", true)]
    [InlineData(StatusCodes.Status207MultiStatus, "text/xml", true)]
    [InlineData(StatusCodes.Status207MultiStatus, "text/plain", false)]
    [InlineData(StatusCodes.Status403Forbidden, "application/xml; charset=utf-8", false)]
    [InlineData(StatusCodes.Status404NotFound, "application/xml; charset=utf-8", false)]
    public void ShouldSanitizeMultiStatus_GatesOnStatusAndContentType(
        int statusCode,
        string contentType,
        bool expected)
    {
        var context = new DefaultHttpContext();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;

        Assert.Equal(expected, PropFindHandlerPatch.ShouldSanitizeMultiStatus(context.Response));
    }

    [Fact]
    public void SanitizePropFindMultiStatus_Removes404PropstatsAndPreserves200Blocks()
    {
        var body = CreateMultiStatusStream(
            """
            <D:response>
              <D:href>/content/</D:href>
              <D:propstat>
                <D:prop><D:displayname>content</D:displayname></D:prop>
                <D:status>HTTP/1.1 200 OK</D:status>
              </D:propstat>
              <D:propstat>
                <D:prop><D:quota-available-bytes/></D:prop>
                <D:status>HTTP/1.1 404 Not Found</D:status>
              </D:propstat>
            </D:response>
            """);

        var sanitized = PropFindHandlerPatch.SanitizePropFindMultiStatus(body);
        var document = XDocument.Parse(Encoding.UTF8.GetString(sanitized));

        Assert.Single(document.Descendants(Dav + "propstat"));
        Assert.Equal("HTTP/1.1 200 OK", document.Descendants(Dav + "status").Single().Value);
        Assert.Equal("/content/", document.Descendants(Dav + "href").Single().Value);
        Assert.Equal("content", document.Descendants(Dav + "displayname").Single().Value);
    }

    [Fact]
    public void SanitizePropFindMultiStatus_Preserves500Propstats()
    {
        var body = CreateMultiStatusStream(
            """
            <D:response>
              <D:href>/locked/</D:href>
              <D:propstat>
                <D:prop><D:displayname>locked</D:displayname></D:prop>
                <D:status>HTTP/1.1 500 Internal Server Error</D:status>
              </D:propstat>
              <D:propstat>
                <D:prop><D:quota-available-bytes/></D:prop>
                <D:status>HTTP/1.1 404 Not Found</D:status>
              </D:propstat>
            </D:response>
            """);

        var sanitized = PropFindHandlerPatch.SanitizePropFindMultiStatus(body);
        var document = XDocument.Parse(Encoding.UTF8.GetString(sanitized));

        Assert.Single(document.Descendants(Dav + "propstat"));
        Assert.Contains(
            document.Descendants(Dav + "status").Select(element => element.Value),
            status => status.StartsWith("HTTP/1.1 500", StringComparison.Ordinal));
    }

    [Fact]
    public void SanitizePropFindMultiStatus_LeavesResponseSkeletonWhenOnly404PropstatsRemoved()
    {
        var body = CreateMultiStatusStream(
            """
            <D:response>
              <D:href>/missing-prop-only/</D:href>
              <D:propstat>
                <D:prop><D:quota-available-bytes/></D:prop>
                <D:status>HTTP/1.1 404 Not Found</D:status>
              </D:propstat>
            </D:response>
            """);

        var sanitized = PropFindHandlerPatch.SanitizePropFindMultiStatus(body);
        var document = XDocument.Parse(Encoding.UTF8.GetString(sanitized));

        Assert.Empty(document.Descendants(Dav + "propstat"));
        Assert.Equal("/missing-prop-only/", document.Descendants(Dav + "href").Single().Value);
    }

    [Fact]
    public void SanitizePropFindMultiStatus_PreservesElementOrderAcrossResponses()
    {
        var body = CreateMultiStatusStream(
            """
            <D:response>
              <D:href>/first/</D:href>
              <D:propstat>
                <D:prop><D:displayname>first</D:displayname></D:prop>
                <D:status>HTTP/1.1 200 OK</D:status>
              </D:propstat>
            </D:response>
            <D:response>
              <D:href>/second/</D:href>
              <D:propstat>
                <D:prop><D:displayname>second</D:displayname></D:prop>
                <D:status>HTTP/1.1 200 OK</D:status>
              </D:propstat>
              <D:propstat>
                <D:prop><D:quota-available-bytes/></D:prop>
                <D:status>HTTP/1.1 404 Not Found</D:status>
              </D:propstat>
            </D:response>
            """);

        var sanitized = PropFindHandlerPatch.SanitizePropFindMultiStatus(body);
        var document = XDocument.Parse(Encoding.UTF8.GetString(sanitized));

        Assert.Equal(["/first/", "/second/"], document.Descendants(Dav + "href").Select(element => element.Value));
        Assert.Equal(2, document.Descendants(Dav + "propstat").Count());
    }

    [Fact]
    public void SanitizePropFindMultiStatus_ReturnsOriginalBytesWhenNo404Propstats()
    {
        var original = CreateMultiStatusStream(
            """
            <D:response>
              <D:href>/unchanged/</D:href>
              <D:propstat>
                <D:prop><D:displayname>unchanged</D:displayname></D:prop>
                <D:status>HTTP/1.1 200 OK</D:status>
              </D:propstat>
            </D:response>
            """);

        var expected = original.ToArray();
        var sanitized = PropFindHandlerPatch.SanitizePropFindMultiStatus(original);

        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void SanitizePropFindMultiStatus_RecomputesContentLength()
    {
        var body = CreateMultiStatusStream(
            """
            <D:response>
              <D:href>/content/</D:href>
              <D:propstat>
                <D:prop><D:displayname>content</D:displayname></D:prop>
                <D:status>HTTP/1.1 200 OK</D:status>
              </D:propstat>
              <D:propstat>
                <D:prop><D:quota-available-bytes/></D:prop>
                <D:status>HTTP/1.1 404 Not Found</D:status>
              </D:propstat>
            </D:response>
            """);

        var originalLength = body.Length;
        var sanitized = PropFindHandlerPatch.SanitizePropFindMultiStatus(body);

        Assert.True(sanitized.Length < originalLength);
        Assert.Equal(sanitized.Length, Encoding.UTF8.GetByteCount(Encoding.UTF8.GetString(sanitized)));
    }

    private static MemoryStream CreateMultiStatusStream(string responsesXml)
    {
        var xml =
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <D:multistatus xmlns:D="DAV:">
             {responsesXml}
             </D:multistatus>
             """;
        return new MemoryStream(Encoding.UTF8.GetBytes(xml));
    }

    private static (DefaultHttpContext Context, MemoryStream Body) CreateContext(
        int statusCode,
        bool hasStarted = false,
        string? contentType = null)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new TestHttpResponseFeature(hasStarted)
        {
            StatusCode = statusCode
        });
        context.Response.Body = CreateResponseBody();
        context.Response.ContentType = contentType;
        return (context, (MemoryStream)context.Response.Body);

        static MemoryStream CreateResponseBody() => new();
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
