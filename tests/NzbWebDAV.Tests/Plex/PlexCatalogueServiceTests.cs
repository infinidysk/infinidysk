using System.Net;
using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Tests.Plex;

public sealed class PlexCatalogueServiceTests
{
    [Fact]
    public async Task ScopedSources_RetainCollectionsWhenHubsFail()
    {
        using var handler = new FakePlexHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/library/sections/2/collections" => PlexApiClientTests.Xml(
                """<MediaContainer><Directory ratingKey="7" key="/library/collections/7/children" title="Shows"/></MediaContainer>"""),
            "/hubs/sections/2" => new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("secret upstream body") },
            _ => PlexApiClientTests.Xml("<MediaContainer/>")
        });
        var catalogue = new PlexCatalogueService(
            new PlexApiClient(new HttpClient(handler), "installation"), new PlexTestClock());

        var snapshot = await catalogue.GetSourcesAsync(PlexApiClientTests.Server(), "2");

        Assert.True(snapshot.IsStale);
        Assert.Equal("collection", Assert.Single(snapshot.Data).Kind);
        Assert.Equal("Plex request failed.", snapshot.Error);
        Assert.DoesNotContain("secret", snapshot.Error!);
    }

    [Fact]
    public async Task ScopedSources_RetainHubsWhenCollectionsFail()
    {
        using var handler = new FakePlexHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/library/sections/2/collections" => new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("secret upstream body") },
            "/hubs/sections/2" => PlexApiClientTests.Xml(
                """<MediaContainer><Hub hubIdentifier="recent" key="/hubs/recent" title="Recent" type="show"/></MediaContainer>"""),
            _ => PlexApiClientTests.Xml("<MediaContainer/>")
        });
        var catalogue = new PlexCatalogueService(
            new PlexApiClient(new HttpClient(handler), "installation"), new PlexTestClock());

        var snapshot = await catalogue.GetSourcesAsync(PlexApiClientTests.Server(), "2");

        Assert.True(snapshot.IsStale);
        Assert.Equal("hub", Assert.Single(snapshot.Data).Kind);
        Assert.Equal("Plex authorization failed; reconnect or update the server token.", snapshot.Error);
        Assert.DoesNotContain("secret", snapshot.Error!);
    }

    [Fact]
    public async Task GlobalSources_RequestOnlyGlobalHubs()
    {
        using var handler = new FakePlexHandler(_ => PlexApiClientTests.Xml(
            """<MediaContainer><Hub hubIdentifier="recent" key="/hubs/recent" title="Recent" type="movie"/></MediaContainer>"""));
        var catalogue = new PlexCatalogueService(
            new PlexApiClient(new HttpClient(handler), "installation"), new PlexTestClock());

        var snapshot = await catalogue.GetSourcesAsync(PlexApiClientTests.Server(), null);

        Assert.False(snapshot.IsStale);
        Assert.Equal("hub", Assert.Single(snapshot.Data).Kind);
        Assert.Equal(new[] { "/hubs" }, handler.Requests.Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Snapshot_RejectsOversizedProjection()
    {
        var items = string.Concat(Enumerable.Range(0, 100).Select(index => $"<Directory key='{index}' type='movie' title='{new string('x', 3000)}'/>"));
        using var handler = new FakePlexHandler(_ => PlexApiClientTests.Xml($"<MediaContainer>{items}</MediaContainer>"));
        var catalogue = new PlexCatalogueService(new PlexApiClient(new HttpClient(handler), "installation"), new PlexTestClock());
        var snapshot = await catalogue.GetLibrariesAsync(PlexApiClientTests.Server());
        Assert.True(snapshot.IsStale);
        Assert.Empty(snapshot.Data);
    }

    [Fact]
    public async Task Snapshots_EvictByAggregateRetainedBytesBeforeEntryCount()
    {
        var items = string.Concat(Enumerable.Range(0, 80).Select(index => $"<Directory key='{index}' type='movie' title='{new string('x', 3000)}'/>"));
        using var handler = new FakePlexHandler(_ => PlexApiClientTests.Xml($"<MediaContainer>{items}</MediaContainer>"));
        var catalogue = new PlexCatalogueService(new PlexApiClient(new HttpClient(handler), "installation"), new PlexTestClock());
        for (var index = 0; index < 40; index++)
            Assert.False((await catalogue.GetLibrariesAsync(PlexApiClientTests.Server() with { Id = index.ToString() })).IsStale);
        await catalogue.GetLibrariesAsync(PlexApiClientTests.Server() with { Id = "0" });
        Assert.Equal(41, handler.Requests.Count);
    }

    [Fact]
    public async Task FailedRefresh_RetainsLastGoodSnapshotWithSafeErrorAndBackoff()
    {
        var failed = false;
        using var handler = new FakePlexHandler(_ => failed
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("secret") }
            : PlexApiClientTests.Xml("""<MediaContainer><Directory key="1" type="movie" title="Movies"/></MediaContainer>"""));
        var clock = new PlexTestClock();
        var catalogue = new PlexCatalogueService(new PlexApiClient(new HttpClient(handler), "installation"), clock);
        var good = await catalogue.GetLibrariesAsync(PlexApiClientTests.Server());
        Assert.False(good.IsStale);
        failed = true;
        clock.Now = clock.Now.AddMinutes(2);
        var stale = await catalogue.GetLibrariesAsync(PlexApiClientTests.Server(), forceRefresh: true);
        Assert.True(stale.IsStale);
        Assert.Equal("Movies", Assert.Single(stale.Data).Title);
        Assert.Equal(good.LastSuccess, stale.LastSuccess);
        Assert.DoesNotContain("secret", stale.Error!);
        await catalogue.GetLibrariesAsync(PlexApiClientTests.Server());
        Assert.Equal(2, handler.Requests.Count);
    }
}
