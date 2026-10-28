using System.Net;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.Metadata;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Catalog;
using NzbWebDAV.Services.SupportPack;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Services.Catalog;

/// <summary>
/// Provider-facing catalog behavior: TMDB normalization, TVDB pagination and token
/// refresh, the TVDB-to-TMDB episode fallback, retry-after-failure of provider reads,
/// missing provider identifiers, and the catalog credentials' masking/redaction.
/// </summary>
public sealed class CatalogMetadataTests
{
    private const string TmdbToken = "tmdb-read-token-value";
    private const string TvdbKey = "tvdb-api-key-value";

    [Fact]
    public async Task Tmdb_TrendingNormalizesArtworkYearAndTypes()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new ScriptedHandler((request, _) =>
        {
            requests.Add(request);
            return Json("""
                {"results":[
                  {"media_type":"movie","id":10331,"title":"Night of the Living Dead","release_date":"1968-10-01",
                   "overview":"Dead walk.","vote_average":7.6,"poster_path":"/poster.jpg","backdrop_path":"/back.jpg"},
                  {"media_type":"tv","id":1930,"name":"The Beverly Hillbillies","first_air_date":"1962-09-26",
                   "overview":null,"vote_average":0,"poster_path":null,"backdrop_path":null},
                  {"media_type":"person","id":42,"name":"Someone"}
                ]}
                """);
        });

        var client = CreateTmdb(handler);
        var items = await client.GetTrendingAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
        var movie = items[0];
        Assert.Equal("movie", movie.Type);
        Assert.Equal(10331, movie.TmdbId);
        Assert.Equal("Night of the Living Dead", movie.Title);
        Assert.Equal(1968, movie.Year);
        Assert.Equal(7.6, movie.Rating);
        Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", movie.PosterUrl);
        Assert.Equal("https://image.tmdb.org/t/p/w1280/back.jpg", movie.BackdropUrl);

        var series = items[1];
        Assert.Equal("series", series.Type);
        Assert.Equal(1962, series.Year);
        Assert.Null(series.Rating);
        Assert.Null(series.PosterUrl);

        var authorization = Assert.Single(requests).Headers.Authorization;
        Assert.NotNull(authorization);
        Assert.Equal("Bearer", authorization.Scheme);
        Assert.Equal(TmdbToken, authorization.Parameter);
    }

    [Fact]
    public async Task Tmdb_SearchDiscardsPeopleAndKeepsProviderOrder()
    {
        var handler = new ScriptedHandler((_, _) => Json("""
            {"page":2,"total_pages":4,"results":[
              {"media_type":"tv","id":1930,"name":"The Beverly Hillbillies","first_air_date":"1962-09-26"},
              {"media_type":"person","id":7,"name":"Buddy Ebsen"},
              {"media_type":"movie","id":10331,"title":"Night of the Living Dead","release_date":"1968-10-01"}
            ]}
            """));

        var page = await CreateTmdb(handler).SearchMultiAsync("hillbillies", 2, CancellationToken.None);

        Assert.Equal(2, page.Page);
        Assert.Equal(4, page.TotalPages);
        Assert.True(page.HasMore);
        Assert.Equal(new[] { "series", "movie" }, page.Results.Select(result => result.Type));
    }

    [Fact]
    public async Task Tmdb_MissingTokenFailsWithoutCallingTheProvider()
    {
        var calls = 0;
        var handler = new ScriptedHandler((_, _) =>
        {
            calls++;
            return Json("{}");
        });
        var client = new TmdbClient(
            new StubHttpClientFactory(handler),
            new ConfigManager());

        var error = await Assert.ThrowsAsync<CatalogException>(
            () => client.GetTrendingAsync(CancellationToken.None));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("Catalog metadata is not configured.", error.SafeMessage);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Tmdb_ProviderFailureIsSanitizedAndNotCached()
    {
        var popularCalls = 0;
        var handler = new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/movie/popular", StringComparison.Ordinal))
            {
                popularCalls++;
                // First attempt fails, the retry succeeds: failures must not be memoized.
                return popularCalls == 1
                    ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                    : Json("""{"results":[{"id":1,"title":"Recovered","release_date":"2001-01-01"}]}""");
            }

            return Json("""{"results":[]}""");
        });
        var catalog = CreateCatalog(handler, tmdbToken: TmdbToken, tvdbKey: null);

        var error = await Assert.ThrowsAsync<CatalogException>(
            () => catalog.GetHomeAsync(CancellationToken.None));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal("The TMDB service is temporarily unavailable.", error.SafeMessage);
        Assert.NotNull(error.RetryAfter);

        var home = await catalog.GetHomeAsync(CancellationToken.None);
        Assert.Equal(
            new[] { "trending", "popular-movies", "popular-series", "top-rated-movies" },
            home.Rows.Select(row => row.Id));
        var popular = home.Rows.Single(row => row.Id == "popular-movies");
        Assert.Equal("Recovered", Assert.Single(popular.Items).Title);
        Assert.Equal(2, popularCalls);
    }

    [Fact]
    public async Task Tvdb_PaginatesByLocalPageNumberAndDeduplicatesEpisodes()
    {
        var requested = new List<string>();
        var handler = new ScriptedHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
                return Json("""{"status":"success","data":{"token":"bearer-one"}}""");

            requested.Add(uri.Query);
            var page = uri.Query.Contains("page=0", StringComparison.Ordinal) ? 0 : 1;
            return page == 0
                ? Json("""
                    {"status":"success",
                     "data":{"episodes":[
                       {"id":10,"seasonNumber":1,"number":1,"name":"The Clampetts Strike Oil","aired":"1962-09-26","runtime":25,"image":"https://art.example/1.jpg","overview":"Oil!"},
                       {"id":11,"seasonNumber":1,"number":2,"name":"Getting Settled","aired":"1962-10-03","runtime":25,"image":null,"overview":null}
                     ]},
                     "links":{"next":"https://api4.thetvdb.com/v4/series/71471/episodes/default?page=1","prev":null}}
                    """)
                : Json("""
                    {"status":"success",
                     "data":{"episodes":[
                       {"id":11,"seasonNumber":1,"number":2,"name":"Getting Settled","aired":"1962-10-03","runtime":25,"image":null,"overview":null},
                       {"id":12,"seasonNumber":1,"number":3,"name":"Meanwhile, Back at the Cabin","aired":"1962-10-10","runtime":30,"image":null,"overview":null}
                     ]},
                     "links":{"next":null,"prev":"https://api4.thetvdb.com/v4/series/71471/episodes/default?page=0"}}
                    """);
        });

        var client = CreateTvdb(handler, TvdbKey);
        var episodes = await client.GetSeasonEpisodesAsync(71471, 1, CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, episodes.Select(episode => episode.EpisodeNumber));
        Assert.Equal("The Clampetts Strike Oil", episodes[0].Name);
        Assert.Equal("https://art.example/1.jpg", episodes[0].StillUrl);
        Assert.Equal(30, episodes[2].RuntimeMinutes);
        // Only local page numbers are requested, never the provider's absolute link.
        Assert.Equal(2, requested.Count);
        Assert.Contains("page=0", requested[0], StringComparison.Ordinal);
        Assert.Contains("page=1", requested[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tvdb_RefreshesTheTokenOnceOn401()
    {
        var log = new List<string>();
        var episodeCalls = 0;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login", StringComparison.Ordinal))
            {
                log.Add("login");
                return Json("""{"status":"success","data":{"token":"bearer-from-login"}}""");
            }

            log.Add("episodes");
            episodeCalls++;
            return episodeCalls == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Json("""
                    {"status":"success","data":{"episodes":[{"id":1,"seasonNumber":1,"number":1,"name":"Pilot"}]},"links":{"next":null}}
                    """);
        });

        var client = CreateTvdb(handler, TvdbKey);
        var episodes = await client.GetSeasonEpisodesAsync(71471, 1, CancellationToken.None);

        Assert.Equal("Pilot", Assert.Single(episodes).Name);
        Assert.Equal(new[] { "login", "episodes", "login", "episodes" }, log);

        // The refreshed token is reused instead of logging in for every request.
        await client.GetSeasonEpisodesAsync(71471, 1, CancellationToken.None);
        Assert.Equal(
            new[] { "login", "episodes", "login", "episodes", "episodes" },
            log);
    }

    [Fact]
    public async Task Tvdb_FailureFallsBackToTheTmdbSeason()
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (path.EndsWith("/login", StringComparison.Ordinal))
                return Json("""{"status":"success","data":{"token":"bearer"}}""");
            if (path.Contains("/series/71471/episodes", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (path.EndsWith("/tv/1930", StringComparison.Ordinal))
            {
                return Json("""
                    {"id":1930,"name":"The Beverly Hillbillies","first_air_date":"1962-09-26","vote_average":6.8,
                     "episode_run_time":[25],"genres":[{"name":"Comedy"}],"poster_path":"/p.jpg","backdrop_path":null,
                     "seasons":[{"season_number":0,"name":"Specials","episode_count":2},
                                {"season_number":1,"name":"Season 1","episode_count":36}]}
                    """);
            }

            if (path.EndsWith("/tv/1930/external_ids", StringComparison.Ordinal))
                return Json("""{"imdb_id":"tt0055662","tvdb_id":71471}""");
            if (path.EndsWith("/tv/1930/season/1", StringComparison.Ordinal))
                return Json("""{"episodes":[{"season_number":1,"episode_number":1,"name":"The Clampetts Strike Oil","air_date":"1962-09-26","runtime":25,"still_path":"/s.jpg"}]}""");

            return Json("{}");
        });
        var catalog = CreateCatalog(handler, tmdbToken: TmdbToken, tvdbKey: TvdbKey);

        var details = await catalog.GetTitleAsync("series", 1930, null, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal(1, details.SelectedSeason);
        Assert.Equal("The Beverly Hillbillies", details.Title);
        Assert.Equal(new[] { "Comedy" }, details.Genres);
        Assert.Equal(new[] { 1 }, details.Episodes.Select(episode => episode.EpisodeNumber));
        Assert.Equal("https://image.tmdb.org/t/p/w500/s.jpg", details.Episodes[0].StillUrl);
    }

    [Fact]
    public async Task Titles_WithoutProviderIdentifiersCannotBePlayed()
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/movie/9", StringComparison.Ordinal))
                return Json("""{"id":9,"title":"No IMDb","release_date":"1999-01-01"}""");
            if (path.EndsWith("/movie/9/external_ids", StringComparison.Ordinal))
                return Json("""{"imdb_id":null}""");
            if (path.EndsWith("/tv/9", StringComparison.Ordinal))
                return Json("""{"id":9,"name":"No Identifiers","first_air_date":"1999-01-01"}""");
            if (path.EndsWith("/tv/9/external_ids", StringComparison.Ordinal))
                return Json("""{"imdb_id":null,"tvdb_id":null}""");
            return Json("{}");
        });
        var catalog = CreateCatalog(handler, tmdbToken: TmdbToken, tvdbKey: TvdbKey);

        var movie = await Assert.ThrowsAsync<CatalogException>(
            () => catalog.GetPlaybackIdentityAsync("movie", 9, null, null, CancellationToken.None));
        Assert.Equal(422, movie.StatusCode);
        Assert.Equal("This movie has no usable IMDb identifier.", movie.SafeMessage);

        var series = await Assert.ThrowsAsync<CatalogException>(
            () => catalog.GetPlaybackIdentityAsync("series", 9, 1, 1, CancellationToken.None));
        Assert.Equal(422, series.StatusCode);
        Assert.Equal("This series has no usable IMDb or TVDB identifier.", series.SafeMessage);
    }

    [Fact]
    public async Task PlaybackIdentityPrefersTvdbForSeriesAndImdbForMovies()
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/movie/10331", StringComparison.Ordinal))
                return Json("""{"id":10331,"title":"Night of the Living Dead","release_date":"1968-10-01"}""");
            if (path.EndsWith("/movie/10331/external_ids", StringComparison.Ordinal))
                return Json("""{"imdb_id":"tt0063350"}""");
            if (path.EndsWith("/tv/1930", StringComparison.Ordinal))
                return Json("""{"id":1930,"name":"The Beverly Hillbillies","first_air_date":"1962-09-26","seasons":[{"season_number":0,"name":"Specials","episode_count":1},{"season_number":1,"name":"Season 1","episode_count":36}]}""");
            if (path.EndsWith("/tv/1930/external_ids", StringComparison.Ordinal))
                return Json("""{"imdb_id":"tt0055662","tvdb_id":71471}""");
            return Json("{}");
        });
        var catalog = CreateCatalog(handler, tmdbToken: TmdbToken, tvdbKey: null);

        var movie = await catalog.GetPlaybackIdentityAsync("movie", 10331, null, null, CancellationToken.None);
        Assert.Equal("movie", movie.Type);
        Assert.Equal("tt0063350", movie.Id);

        var series = await catalog.GetPlaybackIdentityAsync("series", 1930, 1, 1, CancellationToken.None);
        Assert.Equal("series", series.Type);
        Assert.Equal("tvdb:71471:1:1", series.Id);
        Assert.Equal("The Beverly Hillbillies", series.Title);
    }

    [Fact]
    public void CatalogCredentialsAreMaskedRedactedAndEnvironmentMapped()
    {
        var masker = new ConfigSecretMasker("catalog-signing-key");
        foreach (var (key, secret) in new[]
                 {
                     (ConfigKeys.MetadataTmdbReadToken, TmdbToken),
                     (ConfigKeys.MetadataTvdbApiKey, TvdbKey),
                     (ConfigKeys.CatalogProfileToken, "profile-token-value"),
                 })
        {
            var masked = masker.MaskForResponse(key, secret);
            Assert.StartsWith(ConfigSecretMasker.MaskPrefix, masked);
            Assert.DoesNotContain(secret, masked, StringComparison.Ordinal);
            Assert.Equal(secret, masker.ResolveForUpdate(key, masked, secret));

            Assert.Equal("[REDACTED]", new SupportPackRedactor([]).RedactConfigurationValue(key, secret));
        }

        Assert.Equal(
            "NZBDAV_CONFIG__METADATA__TMDB_READ_TOKEN",
            ConfigEnvMapping.ToEnvironmentVariableName(ConfigKeys.MetadataTmdbReadToken));
        Assert.Equal(
            "NZBDAV_CONFIG__METADATA__TVDB_API_KEY",
            ConfigEnvMapping.ToEnvironmentVariableName(ConfigKeys.MetadataTvdbApiKey));
        Assert.Equal(
            "NZBDAV_CONFIG__CATALOG__PROFILE_TOKEN",
            ConfigEnvMapping.ToEnvironmentVariableName(ConfigKeys.CatalogProfileToken));
    }

    [Fact]
    public void CatalogGettersReadPersistedValues()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.MetadataTmdbReadToken, ConfigValue = TmdbToken },
            new ConfigItem { ConfigName = ConfigKeys.MetadataTvdbApiKey, ConfigValue = "  " },
            new ConfigItem { ConfigName = ConfigKeys.CatalogProfileToken, ConfigValue = "profile" },
        ]);

        Assert.Equal(TmdbToken, config.GetTmdbReadToken());
        Assert.Null(config.GetTvdbApiKey());
        Assert.Equal("profile", config.GetCatalogProfileToken());
    }

    private static TmdbClient CreateTmdb(ScriptedHandler handler)
        => new(new StubHttpClientFactory(handler), CreateConfig(TmdbToken));

    private static TvdbClient CreateTvdb(ScriptedHandler handler, string? apiKey)
        => new(new StubHttpClientFactory(handler), CreateConfig(TmdbToken, apiKey));

    private static CatalogService CreateCatalog(ScriptedHandler handler, string? tmdbToken, string? tvdbKey)
    {
        var factory = new StubHttpClientFactory(handler);
        var config = CreateConfig(tmdbToken, tvdbKey);
        return new CatalogService(new TmdbClient(factory, config), new TvdbClient(factory, config));
    }

    private static ConfigManager CreateConfig(string? tmdbToken, string? tvdbKey = null)
    {
        var config = new ConfigManager();
        var items = new List<ConfigItem>();
        if (tmdbToken is not null)
            items.Add(new ConfigItem { ConfigName = ConfigKeys.MetadataTmdbReadToken, ConfigValue = tmdbToken });
        if (tvdbKey is not null)
            items.Add(new ConfigItem { ConfigName = ConfigKeys.MetadataTvdbApiKey, ConfigValue = tvdbKey });
        if (items.Count > 0) config.UpdateValues(items);
        return config;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
