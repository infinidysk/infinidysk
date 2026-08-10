using System.Net;
using System.Xml.Linq;
using NWebDav.Server;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Tests.WebDav;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class DatabaseStoreHttpIntegrationTests(NzbDavWebApplicationFactory factory)
{
    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly XNamespace Dav = WebDavNamespaces.DavNs;

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

        var document = await ReadMultiStatusDocumentAsync(accepted);
        AssertNoNotFoundPropStats(document);
        Assert.All(
            document.Descendants(Dav + "response"),
            response => Assert.Contains(response.Elements(Dav + "propstat"), propStat =>
                propStat.Element(Dav + "status")?.Value.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) is true));

        var hrefs = ReadHrefs(document);
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
        var document = await ReadMultiStatusDocumentAsync(response);
        AssertNoNotFoundPropStats(document);
        var hrefs = ReadHrefs(document);
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

    [Fact]
    public async Task HeadViewIdsFile_UsesFriendlyNameForContentHeaders()
    {
        var item = await AddIdsFileAsync("My.Movie.2024.mkv");
        var itemPath = DatabaseStoreSymlinkFile.GetTargetPath(item.Id, '/');
        var downloadKey = GetWebdavItemRequest.GenerateDownloadKey(
            NzbDavWebApplicationFactory.ApiKey,
            itemPath);

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Head,
            $"/view/{itemPath}?downloadKey={downloadKey}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/x-matroska", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "My.Movie.2024.mkv",
            response.Content.Headers.ContentDisposition?.ToString());
    }

    [Fact]
    public async Task HeadWebDavIdsFile_UsesFriendlyNameForContentHeaders()
    {
        var item = await AddIdsFileAsync("Another.Movie.2025.mkv");
        var itemPath = DatabaseStoreSymlinkFile.GetTargetPath(item.Id, '/');

        using var client = factory.CreateClient();
        using var request = factory.CreateWebDavRequest(HttpMethod.Head, $"/{itemPath}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/x-matroska", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "Another.Movie.2025.mkv",
            response.Content.Headers.ContentDisposition?.ToString());
    }

    private async Task<DavItem> AddIdsFileAsync(string name)
    {
        var item = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            name,
            1024,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            null);
        var file = new DavNzbFile
        {
            Id = item.Id,
            SegmentIds = []
        };
        await factory.AddDavNzbFileAsync(item, file);
        return item;
    }

    private static async Task<XDocument> ReadMultiStatusDocumentAsync(HttpResponseMessage response)
    {
        return await XDocument.LoadAsync(
            await response.Content.ReadAsStreamAsync(),
            LoadOptions.None,
            CancellationToken.None);
    }

    private static string[] ReadHrefs(XDocument document)
    {
        return document
            .Descendants(Dav + "href")
            .Select(element => Uri.UnescapeDataString(element.Value))
            .ToArray();
    }

    private static void AssertNoNotFoundPropStats(XDocument document)
    {
        Assert.DoesNotContain(
            document.Descendants(Dav + "status"),
            status => status.Value.StartsWith("HTTP/1.1 404", StringComparison.Ordinal));
    }
}
