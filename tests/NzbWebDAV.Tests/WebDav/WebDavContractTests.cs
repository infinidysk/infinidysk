using System.Net;
using System.Net.Http.Headers;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.WebDav;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class WebDavContractTests
{
    [Fact]
    public async Task PropFindNzbsAndContent_ReturnsDavCollectionContracts()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateClient();

        using var rejected = new HttpRequestMessage(WebDavContractAssertions.PropFind, "/nzbs");
        rejected.Headers.TryAddWithoutValidation("Depth", "1");
        using var unauthorized = await client.SendAsync(rejected);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var optionsRequest = factory.CreateWebDavRequest(HttpMethod.Options, "/nzbs");
        using var options = await client.SendAsync(optionsRequest);
        Assert.True(
            HasDavOrAllowHeader(options),
            "WebDAV OPTIONS must advertise DAV or Allow. Headers: "
            + string.Join(", ", options.Headers.Select(header => header.Key)));

        using var nzbsRequest = factory.CreateWebDavRequest(
            WebDavContractAssertions.PropFind, "/nzbs", depth: "1");
        using var nzbs = await client.SendAsync(nzbsRequest);
        var nzbsDocument = await WebDavContractAssertions.AssertMultiStatusAsync(nzbs);
        WebDavContractAssertions.AssertCollectionListing(nzbsDocument, "/nzbs");

        using var contentRequest = factory.CreateWebDavRequest(
            WebDavContractAssertions.PropFind, "/content", depth: "1");
        using var content = await client.SendAsync(contentRequest);
        var contentDocument = await WebDavContractAssertions.AssertMultiStatusAsync(content);
        WebDavContractAssertions.AssertCollectionListing(contentDocument, "/content");
    }

    [Fact]
    public async Task HeadAndRangeGet_ReturnDeterministicReadmeBytes()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateClient();

        using var headRequest = factory.CreateWebDavRequest(HttpMethod.Head, "/README");
        using var head = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.True(head.Content.Headers.ContentLength > 10);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        using var getRequest = factory.CreateWebDavRequest(HttpMethod.Get, "/README");
        getRequest.Headers.Range = new RangeHeaderValue(0, 9);
        using var get = await client.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.PartialContent, get.StatusCode);
        Assert.Equal("bytes", get.Headers.AcceptRanges?.ToString());
        var body = await get.Content.ReadAsByteArrayAsync();
        Assert.Equal(10, body.Length);
        Assert.Equal(10, get.Content.Headers.ContentLength);
    }

    private static bool HasDavOrAllowHeader(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("DAV", out var dav) && dav.Any(value => value.Length > 0)
            || response.Headers.TryGetValues("Allow", out var allow)
                && allow.Any(value => value.Contains("PROPFIND", StringComparison.OrdinalIgnoreCase));
    }
}
