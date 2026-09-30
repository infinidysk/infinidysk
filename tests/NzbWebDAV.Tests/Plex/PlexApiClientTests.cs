using System.Net;
using System.Text;
using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Tests.Plex;

public sealed class PlexApiClientTests
{
    [Fact]
    public async Task LibraryMedia_ReadsEveryPageAndProjectsEpisodeMetadata()
    {
        var firstPage = string.Concat(Enumerable.Range(1, 100).Select(index =>
            $"<Video ratingKey='{index}' title='Episode {index}' grandparentTitle='Show' parentIndex='1' index='{index}'><Media><Part file='/plex/Show - S01E{index:000}.mkv'/></Media></Video>"));
        using var handler = new FakePlexHandler(request =>
            request.RequestUri!.Query.Contains("Start=100", StringComparison.Ordinal)
                ? Xml("""<MediaContainer totalSize="101"><Video ratingKey="101" title="Last" grandparentTitle="Show" parentIndex="2" index="1"><Media><Part file="/plex/Last.mkv"/></Media></Video></MediaContainer>""")
                : Xml($"<MediaContainer totalSize='101'>{firstPage}</MediaContainer>"));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        var result = await api.GetLibraryMediaAsync(Server(), new PlexLibrary("1", "TV", "show"));
        Assert.Equal(101, result.Count);
        Assert.Equal("Last.mkv", result[100].FileName);
        Assert.Equal("Show", result[100].ShowName);
        Assert.Equal(2, result[100].Season);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task LibraryMedia_RejectsIncompletePage()
    {
        using var handler = new FakePlexHandler(_ => Xml("""<MediaContainer totalSize="101"><Video ratingKey="1"><Media><Part file="/plex/first.mkv"/></Media></Video></MediaContainer>"""));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        await Assert.ThrowsAsync<PlexRequestException>(() =>
            api.GetLibraryMediaAsync(Server(), new PlexLibrary("1", "TV", "show")));
    }

    [Fact]
    public async Task LibraryMedia_FollowsServerPageCapUntilTotalIsComplete()
    {
        using var handler = new FakePlexHandler(request =>
            request.RequestUri!.Query.Contains("Start=2", StringComparison.Ordinal)
                ? Xml("""<MediaContainer totalSize="3"><Video ratingKey="3"><Media><Part file="/plex/three.mkv"/></Media></Video></MediaContainer>""")
                : Xml("""<MediaContainer totalSize="3"><Video ratingKey="1"><Media><Part file="/plex/one.mkv"/></Media></Video><Video ratingKey="2"><Media><Part file="/plex/two.mkv"/></Media></Video></MediaContainer>"""));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        Assert.Equal(3, (await api.GetLibraryMediaAsync(Server(), new PlexLibrary("1", "Films", "movie"))).Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(true, "next-season")]
    [InlineData(false, "owner-watched")]
    public async Task NextUnwatched_UsesOnlyTheInitiatingUsersVerifiedCredential(bool sameUser, string expected)
    {
        using var handler = new FakePlexHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/library/metadata/show/children" => Xml("""<MediaContainer><Directory ratingKey="s1" index="1"/><Directory ratingKey="s2" index="2"/></MediaContainer>"""),
            "/library/metadata/s1/children" => Xml("""<MediaContainer><Video ratingKey="owner-watched" type="episode" grandparentRatingKey="show" parentIndex="1" index="2" viewCount="1"/></MediaContainer>"""),
            _ => Xml("""<MediaContainer><Video ratingKey="next-season" type="episode" grandparentRatingKey="show" parentIndex="2" index="1" viewCount="0"/></MediaContainer>""")
        });
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        var current = new PlexMediaItem("current", "episode", "Episode", "show", 1, 1, null, 0, 0, null,
            sameUser ? "owner" : "another-user");
        var result = await api.GetNextEpisodesAsync(Server() with { AccountId = "owner" }, current, 1);
        Assert.Equal(expected, Assert.Single(result).RatingKey);
        if (!sameUser) Assert.DoesNotContain(handler.Requests, request => request.Uri.Query.Contains("unwatched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnwatchedShowSource_DoesNotRestartAtAlreadyWatchedEarlySeasons()
    {
        using var handler = new FakePlexHandler(request =>
        {
            Assert.Contains("unwatched=1", request.RequestUri!.Query);
            Assert.EndsWith("/show/allLeaves", request.RequestUri.AbsolutePath);
            return Xml("""<MediaContainer><Video ratingKey="late" type="episode" grandparentRatingKey="show" parentIndex="20" index="1"/></MediaContainer>""");
        });
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        var show = new PlexMediaItem("show", "show", "Show", null, null, null, null, 0, 0, null, "user");
        Assert.Equal("late", Assert.Single(await api.GetNextEpisodesAsync(Server() with { AccountId = "user" }, show, 1)).RatingKey);
    }

    [Fact]
    public async Task UnwatchedShowSource_FiltersSpecialsBeforeApplyingCandidateLimit()
    {
        var specials = string.Concat(Enumerable.Range(1, 20).Select(i => $"<Video ratingKey='special{i}' type='episode' grandparentRatingKey='show' parentIndex='0' index='{i}'/>"));
        using var handler = new FakePlexHandler(_ => Xml($"<MediaContainer>{specials}<Video ratingKey='normal' type='episode' grandparentRatingKey='show' parentIndex='1' index='1'/></MediaContainer>"));
        using var http = new HttpClient(handler);
        var api = new PlexApiClient(http, "installation");
        var show = new PlexMediaItem("show", "show", "Show", null, null, null, null, 0, 0, null, "user");
        Assert.Equal("normal", Assert.Single(await api.GetNextEpisodesAsync(Server() with { AccountId = "user" }, show, 1)).RatingKey);
    }

    [Fact]
    public async Task NextEpisodeWindow_StartsAtCurrentSeasonWithoutOwnersWatchedFilter()
    {
        using var handler = new FakePlexHandler(request =>
        {
            Assert.DoesNotContain("unwatched", request.RequestUri!.Query);
            return request.RequestUri.AbsolutePath.EndsWith("/show/children", StringComparison.Ordinal)
                ? Xml("""<MediaContainer><Directory ratingKey="early" index="1"/><Directory ratingKey="current" index="20"/><Directory ratingKey="next" index="21"/></MediaContainer>""")
                : Xml("""<MediaContainer><Video ratingKey="upcoming" type="episode" grandparentRatingKey="show" parentIndex="20" index="11"/></MediaContainer>""");
        });
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        var current = new PlexMediaItem("watched", "episode", "Episode", "show", 20, 10, null, 0, 0, null);
        Assert.Equal("upcoming", Assert.Single(await api.GetNextEpisodesAsync(Server(), current, 1)).RatingKey);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath.Contains("early", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SourcePreview_HonorsConfiguredLimitsAboveTwoHundred()
    {
        var items = string.Concat(Enumerable.Range(0, 100).Select(index => $"<Video ratingKey='{index}' type='movie' title='Film'/>"));
        using var handler = new FakePlexHandler(_ => Xml($"<MediaContainer totalSize='1000'>{items}</MediaContainer>"));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        Assert.Equal(250, (await api.GetPreviewAsync(Server(), "/hubs/home/recentlyAdded", 250)).Count);
    }

    [Fact]
    public async Task CataloguePagination_OnlyProjectsDirectContainerItems()
    {
        var nested = string.Concat(Enumerable.Repeat("<Directory key='1' type='movie' title='Nested'>", 100))
            + string.Concat(Enumerable.Repeat("</Directory>", 100));
        using var handler = new FakePlexHandler(_ => Xml($"<MediaContainer>{nested}</MediaContainer>"));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        Assert.Single(await api.GetLibrariesAsync(Server()));
    }

    [Fact]
    public async Task CatalogueProjection_RejectsOversizedTitle()
    {
        using var handler = new FakePlexHandler(_ => Xml($"<MediaContainer><Directory key='1' type='movie' title='{new string('x', 8192)}'/></MediaContainer>"));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        await Assert.ThrowsAsync<PlexRequestException>(() => api.GetLibrariesAsync(Server()));
    }

    [Fact]
    public async Task Pagination_BoundsAggregateXmlBytes()
    {
        using var handler = new FakePlexHandler(_ => Xml($"<MediaContainer totalSize='2'><Directory key='1' type='movie' title='Movie'/><!--{new string('x', 2200000)}--></MediaContainer>"));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        await Assert.ThrowsAsync<PlexRequestException>(() => api.GetLibrariesAsync(Server()));
    }

    [Fact]
    public async Task Metadata_RejectsDifferentRatingKeyRatherThanEnrichingWrongItem()
    {
        using var handler = new FakePlexHandler(_ => Xml("""<MediaContainer><Video ratingKey="other" type="movie"/></MediaContainer>"""));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        await Assert.ThrowsAsync<PlexRequestException>(() => api.GetMetadataAsync(Server(), "7"));
    }

    [Fact]
    public async Task HomeUsersAndSwitch_KeepPinAndTokensOutOfUrls_AndMetadataEnrichesFiles()
    {
        string? switchBody = null;
        using var handler = new FakePlexHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/switch", StringComparison.Ordinal))
            {
                switchBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"authToken":"managed-secret"}""") };
            }
            if (request.RequestUri.AbsolutePath == "/api/v2/home/users")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"users":[{"id":9,"uuid":"user-9","title":"Child","protected":true,"admin":false}]}""") };
            if (request.RequestUri.AbsolutePath == "/api/v2/user")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":9,"uuid":"user-9","username":"Child"}""") };
            return Xml("""<MediaContainer><Video ratingKey="7" type="movie"><Media><Part file="/movie.mkv"/></Media></Video></MediaContainer>""");
        });
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        var user = Assert.Single(await api.GetHomeUsersAsync("account-secret"));
        Assert.Equal("user-9", user.Id);
        Assert.True(user.Protected);
        Assert.Equal("managed-secret", await api.SwitchHomeUserAsync("account-secret", user.Id, "1234"));
        Assert.Equal("pin=1234", switchBody);
        Assert.Equal("user-9", (await api.GetAccountAsync("managed-secret")).Id);
        Assert.Equal("/movie.mkv", (await api.GetMetadataAsync(Server(), "7")).File);
        Assert.All(handler.Requests, request =>
        {
            Assert.DoesNotContain("1234", request.Uri.ToString());
            Assert.DoesNotContain("secret", request.Uri.ToString());
        });
    }

    [Fact]
    public async Task Timeout_BoundsResponseBodyAfterHeadersHaveArrived()
    {
        using var handler = new FakePlexHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new WaitingBodyStream()) });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(25) };
        var api = new PlexApiClient(http, "installation");
        using var cancellation = new CancellationTokenSource();
        try
        {
            await Assert.ThrowsAsync<PlexRequestException>(() => api.GetLibrariesAsync(Server(), cancellation.Token)
                .WaitAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally { cancellation.Cancel(); }
    }

    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY leak SYSTEM 'file:///etc/passwd'>]><MediaContainer>&leak;</MediaContainer>")]
    [InlineData("<MediaContainer><broken secret")]
    public async Task XmlEntityExpansionAndMalformedBodies_AreRejectedWithoutBodyLeak(string xml)
    {
        using var handler = new FakePlexHandler(_ => Xml(xml));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        var exception = await Assert.ThrowsAsync<PlexRequestException>(() => api.GetLibrariesAsync(Server()));
        Assert.DoesNotContain("secret", exception.ToString());
        Assert.DoesNotContain("passwd", exception.ToString());
    }

    [Fact]
    public async Task OversizedResponse_IsRejectedBeforeParsing()
    {
        using var handler = new FakePlexHandler(_ => Xml(new string('x', 4 * 1024 * 1024 + 1)));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        await Assert.ThrowsAsync<PlexRequestException>(() => api.GetLibrariesAsync(Server()));
    }

    [Fact]
    public async Task TestServer_RequiresAuthorizedEndpointBeyondPublicIdentity()
    {
        using var handler = new FakePlexHandler(request => request.RequestUri!.AbsolutePath == "/identity"
            ? Xml("""<MediaContainer machineIdentifier="machine"/>""")
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        await Assert.ThrowsAsync<PlexRequestException>(() => api.TestServerAsync(Server()));
    }

    [Fact]
    public async Task History_PreservesAccountIdentityAndIgnoresOlderEvents()
    {
        using var handler = new FakePlexHandler(_ => Xml("""<MediaContainer><Video ratingKey="1" accountID="7" viewedAt="1800000010"/><Video ratingKey="2" accountID="8" viewedAt="1700000000"/></MediaContainer>"""));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        var item = Assert.Single(await api.GetHistoryAsync(Server(), DateTimeOffset.FromUnixTimeSeconds(1800000000)));
        Assert.Equal("7", item.UserId);
    }

    [Fact]
    public async Task LibrariesUsersSourcesAndEpisodes_UseTypedBoundedXmlContracts()
    {
        using var handler = new FakePlexHandler(request => Xml(request.RequestUri!.AbsolutePath switch
        {
            "/library/sections" => """<MediaContainer><Directory key="2" title="TV" type="show"/></MediaContainer>""",
            "/accounts" => """<MediaContainer><Account id="4" name="viewer"/></MediaContainer>""",
            "/library/sections/2/collections" => """<MediaContainer><Directory ratingKey="7" key="/library/collections/7/children" title="Shows"/></MediaContainer>""",
            "/hubs/sections/2" => """<MediaContainer><Hub hubIdentifier="recent" key="/hubs/recent" title="Recent" type="show"/></MediaContainer>""",
            _ => """<MediaContainer><Video ratingKey="9" grandparentRatingKey="8" parentIndex="3" index="1" type="episode" title="Next"/></MediaContainer>"""
        }));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        Assert.Equal("2", Assert.Single(await api.GetLibrariesAsync(Server())).Id);
        Assert.Equal("4", Assert.Single(await api.GetUsersAsync(Server())).Id);
        var sources = (await api.GetCollectionsAsync(Server(), "2"))
            .Concat(await api.GetHubsAsync(Server(), "2")).ToArray();
        Assert.Equal(new[] { "collection", "hub" }, sources.Select(source => source.Kind));
        Assert.Equal("recent", sources[1].Id);
        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/hubs/sections/2");
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath == "/library/sections/2/hubs");
        Assert.Equal(3, Assert.Single(await api.GetNextEpisodesAsync(Server(), "8", 2)).Season);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.ToString().Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sessions_SelectsMarkedMediaAndPart_AndSendsTokenOnlyInHeader()
    {
        using var handler = new FakePlexHandler(_ => Xml("""
            <MediaContainer><Video ratingKey="7" type="episode" grandparentRatingKey="3" parentIndex="2" index="1" viewOffset="12000" duration="60000">
              <User id="9" title="viewer"/><Player state="playing"/><Session id="session"/>
              <Media><Part file="/wrong.mkv"/></Media>
              <Media selected="1"><Part file="/wrong-part.mkv"/><Part selected="1" file="/right.mkv"/></Media>
            </Video></MediaContainer>
            """));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        var result = await api.GetSessionsAsync(Server());
        var session = Assert.Single(result);
        Assert.Equal("/right.mkv", session.File);
        Assert.Equal("9", session.UserId);
        Assert.Equal("playing", session.State);
        Assert.All(handler.Requests, request =>
        {
            Assert.DoesNotContain("secret", request.Uri.ToString());
            Assert.Equal("secret", request.Token);
        });
    }

    [Theory]
    [InlineData("https://evil.example/library/1")]
    [InlineData("//evil.example/library/1")]
    [InlineData("/library/../identity")]
    [InlineData("/library/1?X-Plex-Token=leak")]
    [InlineData("/library/%2e%2e/identity")]
    public async Task Preview_RejectsUnsafeKeysBeforeSendingCredentials(string key)
    {
        using var handler = new FakePlexHandler(_ => Xml("<MediaContainer/>"));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        await Assert.ThrowsAsync<ArgumentException>(() => api.GetPreviewAsync(Server(), key, 10));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Pagination_IsBounded_AndNestedHubItemsAreParsed()
    {
        using var handler = new FakePlexHandler(_ => Xml("""
            <MediaContainer totalSize="100000"><Hub><Video ratingKey="1" type="movie" title="Film"/></Hub></MediaContainer>
            """));
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        var items = await api.GetPreviewAsync(Server(), "/hubs/home/recentlyAdded", 3);
        Assert.Equal(3, items.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(items, item => Assert.Equal("1", item.RatingKey));
    }

    [Fact]
    public async Task Errors_DoNotExposeUpstreamBodyOrToken_AndRedirectIsNotFollowed()
    {
        using var handler = new FakePlexHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evil.example/?token=secret") },
            Content = new StringContent("secret upstream body")
        });
        var api = new PlexApiClient(new HttpClient(handler), "installation");
        var exception = await Assert.ThrowsAsync<PlexRequestException>(() => api.GetLibrariesAsync(Server()));
        Assert.DoesNotContain("secret", exception.ToString());
        Assert.Single(handler.Requests);
    }

    internal static PlexServer Server() => new() { Id = "machine", Name = "Plex", Url = "http://192.168.1.2:32400", Token = "secret" };
    internal static HttpResponseMessage Xml(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/xml") };
}

internal sealed class FakePlexHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(Uri Uri, string? Token)> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!, request.Headers.TryGetValues("X-Plex-Token", out var values) ? values.Single() : null));
        return Task.FromResult(respond(request));
    }
}

internal sealed class WaitingBodyStream : MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}
