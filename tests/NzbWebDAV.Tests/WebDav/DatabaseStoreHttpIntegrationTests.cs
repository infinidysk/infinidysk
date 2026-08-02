using System.Net;
using System.Xml.Linq;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.WebDav;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class DatabaseStoreHttpIntegrationTests(NzbDavWebApplicationFactory factory)
{
    private static readonly HttpMethod PropFind = new("PROPFIND");

    [Fact]
    public async Task PropFindRoot_RequiresBasicAuthenticationAndListsCoreMounts()
    {
        using var client = factory.CreateClient();
        using var rejectedRequest = new HttpRequestMessage(PropFind, "/");
        rejectedRequest.Headers.TryAddWithoutValidation("Depth", "1");

        using var rejected = await client.SendAsync(rejectedRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        using var acceptedRequest = factory.CreateWebDavRequest(PropFind, "/", depth: "1");
        using var accepted = await client.SendAsync(acceptedRequest);
        Assert.Equal((HttpStatusCode)207, accepted.StatusCode);

        var hrefs = await ReadHrefsAsync(accepted);
        Assert.Contains(hrefs, href => href.EndsWith("/nzbs", StringComparison.Ordinal));
        Assert.Contains(hrefs, href => href.EndsWith("/content", StringComparison.Ordinal));
        Assert.Contains(hrefs, href => href.EndsWith("/completed-symlinks", StringComparison.Ordinal));
        Assert.Contains(hrefs, href => href.EndsWith("/.ids", StringComparison.Ordinal));
        Assert.Contains(hrefs, href => href.EndsWith("/README", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PropFindNestedPersistedCollection_ResolvesAndListsChildren()
    {
        var library = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "integration-library",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null);
        var title = DavItem.New(
            Guid.NewGuid(),
            library,
            "deterministic-title",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null);
        await factory.AddDavItemsAsync(library, title);

        using var client = factory.CreateClient();
        using var request = factory.CreateWebDavRequest(
            PropFind,
            "/content/integration-library",
            depth: "1");
        using var response = await client.SendAsync(request);

        Assert.Equal((HttpStatusCode)207, response.StatusCode);
        var hrefs = await ReadHrefsAsync(response);
        Assert.Contains(
            hrefs,
            href => href.EndsWith(
                "/content/integration-library/deterministic-title",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task HeadReadme_ReturnsDeterministicEmbeddedFileMetadata()
    {
        using var client = factory.CreateClient();
        using var request = factory.CreateWebDavRequest(HttpMethod.Head, "/README");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentLength > 0);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task<string[]> ReadHrefsAsync(HttpResponseMessage response)
    {
        var document = await XDocument.LoadAsync(
            await response.Content.ReadAsStreamAsync(),
            LoadOptions.None,
            CancellationToken.None);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "href")
            .Select(element => Uri.UnescapeDataString(element.Value))
            .ToArray();
    }
}
