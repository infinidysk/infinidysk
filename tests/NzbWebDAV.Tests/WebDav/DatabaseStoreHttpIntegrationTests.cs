using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using NWebDav.Server;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services.StreamTrace;
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

    [Fact]
    public async Task GetViewRanges_TraceRequestLocalBytesAndTimingsInSameSession()
    {
        var trace = factory.Services.GetRequiredService<StreamTraceBuffer>();
        var wasEnabled = trace.Enabled;
        if (!wasEnabled)
            trace.EnableFor(TimeSpan.Zero, 1000, StreamTraceBuffer.SourceEnv);
        try
        {
            const string itemPath = "README";
            var downloadKey = GetWebdavItemRequest.GenerateDownloadKey(
                NzbDavWebApplicationFactory.ApiKey, itemPath);
            var playerSession = Guid.NewGuid().ToString("N");
            var userAgent = $"view-trace-test-{playerSession}";
            using var client = factory.CreateClient();
            foreach (var (start, end) in new[] { (0, 99), (100, 299) })
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"/view/{itemPath}?downloadKey={downloadKey}&playerSession={playerSession}");
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
                Assert.Equal(end - start + 1, (await response.Content.ReadAsByteArrayAsync()).Length);
            }

            var events = trace.ListSessions(500)
                .SelectMany(session => trace.GetSessionEvents(session.SessionId)).ToArray();
            var ranges = events.Where(entry => entry.Kind == "RangeOpen" && entry.UserAgent == userAgent).ToArray();
            Assert.Equal(2, ranges.Length);
            Assert.Equal(ranges[0].SessionId, ranges[1].SessionId);
            foreach (var range in ranges)
            {
                var ended = Assert.Single(events, entry =>
                    entry.Kind == "RangeEnd" && entry.RangeGeneration == range.RangeGeneration);
                Assert.Equal(range.RangeEnd - range.RangeStart + 1, ended.BytesServed);
                var timing = Assert.Single(events, entry =>
                    entry.Kind == "RequestEnd" && entry.RangeGeneration == range.RangeGeneration);
                Assert.NotNull(timing.FirstByteMs);
                Assert.True(timing.RequestDurationMs - timing.FirstByteMs >= 0);
                Assert.True(timing.CleanupMs >= 0);
                Assert.Null(timing.CancelledAtMs);
            }
        }
        finally
        {
            if (!wasEnabled)
                trace.StopRecording();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetView_OverlongMultipartContent_RespectsContentLength(bool sharedStreamsEnabled)
    {
        const int declaredFileSize = 262_135;
        var packedBytes = Enumerable.Range(0, declaredFileSize + 9)
            .Select(index => (byte)(index % 251)).ToArray();
        await using var testFactory = NzbDavWebApplicationFactory.CreateWithFakeNntp(
            new Dictionary<string, byte[]> { ["issue-1591-volume"] = packedBytes });
        var config = testFactory.Services.GetRequiredService<ConfigManager>();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetSharedStreamsEnabled,
                ConfigValue = sharedStreamsEnabled ? "true" : "false",
            },
        ]);
        Assert.Equal(sharedStreamsEnabled, config.IsSharedStreamsEnabled());
        Assert.False(config.IsFiniteRangeSchedulerEnabled());

        var trace = testFactory.Services.GetRequiredService<StreamTraceBuffer>();
        trace.EnableFor(TimeSpan.Zero, 1000, StreamTraceBuffer.SourceEnv);
        var itemPath = await AddOverlongMultipartFileAsync(
            testFactory, declaredFileSize, packedBytes.Length);
        var downloadKey = GetWebdavItemRequest.GenerateDownloadKey(
            NzbDavWebApplicationFactory.ApiKey, itemPath);
        using var client = testFactory.CreateClient();
        var cases = new (string? RangeHeader, int Start, int Count)[]
        {
            (null, 0, declaredFileSize),
            ("bytes=62144-", 62_144, 199_991),
            ("bytes=258752-", 258_752, 3_383),
            ("bytes=100-199", 100, 100),
            ("bytes=-3383", 258_752, 3_383),
        };

        foreach (var testCase in cases)
        {
            var userAgent = $"view-length-test-{Guid.NewGuid():N}";
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"/view/{itemPath}?downloadKey={downloadKey}");
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            if (testCase.RangeHeader is not null)
                request.Headers.TryAddWithoutValidation("Range", testCase.RangeHeader);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(
                testCase.RangeHeader is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent,
                response.StatusCode);
            Assert.Equal((long)testCase.Count, response.Content.Headers.ContentLength);
            string? expectedContentRange = testCase.RangeHeader is null
                ? null
                : $"bytes {testCase.Start}-{testCase.Start + testCase.Count - 1}/{declaredFileSize}";
            Assert.Equal(expectedContentRange, response.Content.Headers.ContentRange?.ToString());
            Assert.Equal(testCase.Count, body.Length);
            Assert.Equal(packedBytes[testCase.Start..(testCase.Start + testCase.Count)], body);

            var events = trace.ListSessions(500)
                .SelectMany(session => trace.GetSessionEvents(session.SessionId)).ToArray();
            var opened = Assert.Single(events,
                entry => entry.Kind == "RangeOpen" && entry.UserAgent == userAgent);
            var ended = Assert.Single(events, entry =>
                entry.Kind == "RangeEnd" && entry.SessionId == opened.SessionId &&
                entry.RangeGeneration == opened.RangeGeneration);
            Assert.Equal((long)testCase.Count, ended.BytesServed);
            Assert.Equal("Completed", ended.EndReason);
            long? expectedTraceFileSize = testCase.RangeHeader is null ||
                testCase.RangeHeader.EndsWith('-')
                ? declaredFileSize
                : null;
            Assert.Equal(expectedTraceFileSize, opened.FileSize);
        }
    }

    private static async Task<string> AddOverlongMultipartFileAsync(
        NzbDavWebApplicationFactory testFactory, int declaredFileSize, int packedLength)
    {
        var item = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, "synthetic-overlong.mkv", declaredFileSize,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.MultipartFile,
            null, null, null, null);
        var multipart = new DavMultipartFile
        {
            Id = item.Id,
            Metadata = new DavMultipartFile.Meta
            {
                ExpectedFileSize = declaredFileSize,
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["issue-1591-volume"],
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, packedLength),
                        FilePartByteRange = LongRange.FromStartAndSize(0, packedLength),
                    },
                ],
            },
        };
        using var scope = testFactory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        context.Items.Add(item);
        context.MultipartFiles.Add(multipart);
        await context.SaveChangesAsync();
        return DatabaseStoreSymlinkFile.GetTargetPath(item.Id, '/');
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
