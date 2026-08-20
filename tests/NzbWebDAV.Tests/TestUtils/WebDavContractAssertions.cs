using System.Net;
using System.Xml.Linq;
using NWebDav.Server;

namespace NzbWebDAV.Tests.TestUtils;

internal static class WebDavContractAssertions
{
    private static readonly XNamespace Dav = WebDavNamespaces.DavNs;
    public static readonly HttpMethod PropFind = new("PROPFIND");

    public static async Task<XDocument> AssertMultiStatusAsync(HttpResponseMessage response)
    {
        Assert.Equal((HttpStatusCode)207, response.StatusCode);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.NotNull(mediaType);
        Assert.Contains("xml", mediaType, StringComparison.OrdinalIgnoreCase);
        var document = await XDocument.LoadAsync(
            await response.Content.ReadAsStreamAsync(),
            LoadOptions.None,
            CancellationToken.None);
        Assert.Equal(Dav + "multistatus", document.Root?.Name);
        return document;
    }

    public static void AssertCollectionListing(XDocument document, params string[] hrefSuffixes)
    {
        var hrefs = document
            .Descendants(Dav + "href")
            .Select(element => Uri.UnescapeDataString(element.Value))
            .ToArray();
        Assert.NotEmpty(hrefs);
        foreach (var suffix in hrefSuffixes)
        {
            Assert.Contains(
                hrefs,
                href => href.EndsWith(suffix, StringComparison.Ordinal));
        }

        foreach (var response in document.Descendants(Dav + "response"))
        {
            Assert.False(string.IsNullOrWhiteSpace(response.Element(Dav + "href")?.Value));
            var okPropStat = response.Elements(Dav + "propstat").FirstOrDefault(propStat =>
                propStat.Element(Dav + "status")?.Value.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) is true);
            Assert.NotNull(okPropStat);
            var prop = okPropStat!.Element(Dav + "prop");
            Assert.NotNull(prop);
            Assert.NotNull(prop!.Element(Dav + "displayname"));
            Assert.NotNull(prop.Element(Dav + "resourcetype"));
        }

        Assert.DoesNotContain(
            document.Descendants(Dav + "status"),
            status => status.Value.StartsWith("HTTP/1.1 404", StringComparison.Ordinal));
    }
}
