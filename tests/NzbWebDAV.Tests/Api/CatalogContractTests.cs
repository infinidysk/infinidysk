using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NzbWebDAV.Clients.Metadata;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.Catalog;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

/// <summary>
/// Catalog wire contract: authentication, schema shape, coordinate validation, the
/// no-release outcome, and the HLS manifest/segment content types.
/// </summary>
[Collection(nameof(HttpIntegrationCollection))]
public sealed class CatalogContractTests(NzbDavWebApplicationFactory factory)
{
    private const string ProfileToken = "catalog-contract-profile-token";

    [Fact]
    public async Task CatalogEndpointsRequireAuthentication()
    {
        using var host = CreateHost();
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/playback")
        {
            Content = new StringContent("""{"type":"movie","tmdbId":1}""", Encoding.UTF8, "application/json"),
        };

        foreach (var path in new[]
                 {
                     "/api/catalog/home",
                     "/api/catalog/search?q=night",
                     "/api/catalog/titles/movie/10331",
                 })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var playback = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, playback.StatusCode);

        using var manifest = await client.GetAsync(
            $"/api/catalog/playback/{Guid.NewGuid():D}/playlist.m3u8");
        Assert.Equal(HttpStatusCode.Unauthorized, manifest.StatusCode);

        using var segment = await client.GetAsync(
            $"/api/catalog/playback/{Guid.NewGuid():D}/segments/0.ts");
        Assert.Equal(HttpStatusCode.Unauthorized, segment.StatusCode);
    }

    [Fact]
    public async Task HomeResponseMatchesContract()
    {
        using var host = CreateHost();
        using var client = CreateCatalogClient(host);

        using var response = await client.GetAsync("/api/catalog/home");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonContractValidator.AssertMatchesSchema(json.RootElement, "admin/v1/catalog-home.schema.json");

        var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "trending", "popular-movies", "popular-series", "top-rated-movies" },
            rows.Select(row => row.GetProperty("id").GetString()));
        var trending = rows[0].GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("movie", trending[0].GetProperty("type").GetString());
        Assert.Equal(10331, trending[0].GetProperty("tmdbId").GetInt32());
        Assert.Equal(
            "https://image.tmdb.org/t/p/w500/poster.jpg",
            trending[0].GetProperty("posterUrl").GetString());
        Assert.Equal("series", trending[1].GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("/api/catalog/search?q=")]
    [InlineData("/api/catalog/search?q=night&page=0")]
    [InlineData("/api/catalog/search?q=night&page=21")]
    [InlineData("/api/catalog/search?q=night&page=abc")]
    public async Task SearchRejectsInvalidQueryAndPagination(string path)
    {
        using var host = CreateHost();
        using var client = CreateCatalogClient(host);

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(ProblemDetail(json)));
    }

    [Fact]
    public async Task SearchResponseMatchesContract()
    {
        using var host = CreateHost();
        using var client = CreateCatalogClient(host);

        using var response = await client.GetAsync("/api/catalog/search?q=night&page=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonContractValidator.AssertMatchesSchema(json.RootElement, "admin/v1/catalog-search.schema.json");
        Assert.Equal(1, json.RootElement.GetProperty("page").GetInt32());
        Assert.False(json.RootElement.GetProperty("hasMore").GetBoolean());
        // People are discarded from the mixed search feed.
        Assert.Equal(1, json.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task TitleDetailsMatchContract()
    {
        using var host = CreateHost();
        using var client = CreateCatalogClient(host);

        using var response = await client.GetAsync("/api/catalog/titles/series/1930?season=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonContractValidator.AssertMatchesSchema(json.RootElement, "admin/v1/catalog-title.schema.json");
        Assert.Equal(1, json.RootElement.GetProperty("selectedSeason").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("seasons").GetArrayLength());
        Assert.Equal(
            "The Clampetts Strike Oil",
            json.RootElement.GetProperty("episodes")[0].GetProperty("name").GetString());
        Assert.Equal(
            "https://image.tmdb.org/t/p/w500/still.jpg",
            json.RootElement.GetProperty("episodes")[0].GetProperty("stillUrl").GetString());
    }

    [Fact]
    public async Task TitleRejectsInvalidTypeSeasonAndUnknownIds()
    {
        using var host = CreateHost();
        using var client = CreateCatalogClient(host);

        foreach (var path in new[]
                 {
                     "/api/catalog/titles/person/1930",
                     "/api/catalog/titles/movie/10331?season=-1",
                 })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var missing = await client.GetAsync("/api/catalog/titles/movie/999999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var json = await JsonDocument.ParseAsync(await missing.Content.ReadAsStreamAsync());
        Assert.Equal("Title not found.", ProblemDetail(json));
    }

    [Theory]
    [InlineData("""{"type":"movie","tmdbId":1,"season":1}""")]
    [InlineData("""{"type":"movie","tmdbId":1,"episode":1}""")]
    [InlineData("""{"type":"series","tmdbId":1,"episode":1}""")]
    [InlineData("""{"type":"series","tmdbId":1,"season":1}""")]
    [InlineData("""{"type":"series","tmdbId":1,"season":0,"episode":0}""")]
    [InlineData("""{"type":"clip","tmdbId":1}""")]
    [InlineData("""{"type":"movie","tmdbId":0}""")]
    [InlineData("{oops")]
    public async Task PlaybackRejectsMalformedCoordinates(string body)
    {
        using var host = CreateHost();
        using var client = CreateCatalogClient(host);

        using var response = await client.PostAsync(
            "/api/catalog/playback",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(ProblemDetail(json)));
    }

    [Fact]
    public async Task PlaybackWithoutReleasesReturns404()
    {
        using var host = CreateHost();
        using var client = CreateCatalogClient(host);
        ConfigureCatalogPlayback(host);

        using var response = await client.PostAsync(
            "/api/catalog/playback",
            new StringContent("""{"type":"movie","tmdbId":10331}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("No Usenet releases found for this title.", ProblemDetail(json));
    }

    [Fact]
    public async Task PlaybackWithoutConfiguredProfileReturns503()
    {
        using var host = CreateHost();
        using var client = CreateCatalogClient(host);

        using var response = await client.PostAsync(
            "/api/catalog/playback",
            new StringContent("""{"type":"movie","tmdbId":10331}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("Catalog playback is not configured.", ProblemDetail(json));
    }

    [Fact]
    public async Task ManifestAndSegmentServeTheirMediaTypes()
    {
        var runner = new RecordingFFmpegRunner();
        using var host = CreateHost(runner);
        using var client = CreateCatalogClient(host);
        var sessionId = SeedPlaybackSession(host, TimeSpan.FromSeconds(12.5));

        using var manifest = await client.GetAsync(
            $"/api/catalog/playback/{sessionId:D}/playlist.m3u8");

        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", manifest.Content.Headers.ContentType?.MediaType);
        Assert.True(manifest.Headers.CacheControl?.NoStore);
        var playlist = await manifest.Content.ReadAsStringAsync();
        Assert.StartsWith("#EXTM3U", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-ENDLIST", playlist, StringComparison.Ordinal);
        // Segments carry contiguous absolute timestamps, so the playlist has no discontinuities.
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXTINF:0.500,", playlist, StringComparison.Ordinal);

        using var segment = await client.GetAsync($"/api/catalog/playback/{sessionId:D}/segments/2.ts");
        Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
        Assert.Equal("video/mp2t", segment.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await segment.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PlaybackStreamRejectsUnknownSessionsAndBadIndexes()
    {
        var runner = new RecordingFFmpegRunner();
        using var host = CreateHost(runner);
        using var client = CreateCatalogClient(host);
        var sessionId = SeedPlaybackSession(host, TimeSpan.FromSeconds(12.5));

        using var unknown = await client.GetAsync($"/api/catalog/playback/{Guid.NewGuid():D}/playlist.m3u8");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var unknownJson = await JsonDocument.ParseAsync(await unknown.Content.ReadAsStreamAsync());
        Assert.Equal("Playback session expired. Start playback again.", ProblemDetail(unknownJson));

        using var negative = await client.GetAsync($"/api/catalog/playback/{sessionId:D}/segments/-1.ts");
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);

        using var beyond = await client.GetAsync($"/api/catalog/playback/{sessionId:D}/segments/9.ts");
        Assert.Equal(HttpStatusCode.NotFound, beyond.StatusCode);
    }

    private WebApplicationFactory<Program> CreateHost(IFFmpegRunner? ffmpegRunner = null)
        => factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddHttpClient(MetadataHttp.TmdbClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new ScriptedHandler((request, _) => FakeTmdbResponse(request)));
                if (ffmpegRunner is null) return;
                services.RemoveAll<IFFmpegRunner>();
                services.AddSingleton(ffmpegRunner);
            });
        });

    private static HttpClient CreateCatalogClient(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);
        var configManager = host.Services.GetRequiredService<ConfigManager>();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.MetadataTmdbReadToken, ConfigValue = "test-read-token" },
        ]);
        return client;
    }

    private static void ConfigureCatalogPlayback(WebApplicationFactory<Program> host)
    {
        host.Services.GetRequiredService<ConfigManager>().UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.CatalogProfileToken, ConfigValue = ProfileToken },
            new ConfigItem
            {
                ConfigName = ConfigKeys.ProfilesInstances,
                ConfigValue = $$"""
                    {"Profiles":[{"Token":"{{ProfileToken}}","Name":"Catalog","IndexerNames":[],"QualitySort":"Web1080p"}]}
                    """,
            },
        ]);
    }

    private static Guid SeedPlaybackSession(WebApplicationFactory<Program> host, TimeSpan duration)
    {
        var store = host.Services.GetRequiredService<CatalogPlaybackSessionStore>();
        var session = store.Add(
            "http://localhost:3000/api/search/token/play/play.mkv",
            "http://127.0.0.1:8080/api/search/token/play/play.mkv",
            "movie",
            "tt0063350",
            "Night.of.the.Living.Dead.1968.1080p.WEB-DL.x264",
            "");
        // Pre-resolved so the contract test never resolves or probes over the network.
        session.ResolvedViewUri = "http://127.0.0.1:8080/view/content/night.mkv?downloadKey=k";
        session.MediaDuration = duration;
        return session.Id;
    }

    private static HttpResponseMessage FakeTmdbResponse(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/trending/all/week", StringComparison.Ordinal))
        {
            return Json("""
                {"results":[
                  {"media_type":"movie","id":10331,"title":"Night of the Living Dead","release_date":"1968-10-01","overview":"Dead walk.","vote_average":7.6,"poster_path":"/poster.jpg","backdrop_path":"/back.jpg"},
                  {"media_type":"tv","id":1930,"name":"The Beverly Hillbillies","first_air_date":"1962-09-26","overview":null,"vote_average":6.8,"poster_path":null,"backdrop_path":null}
                ]}
                """);
        }

        if (path.EndsWith("/movie/popular", StringComparison.Ordinal)
            || path.EndsWith("/movie/top_rated", StringComparison.Ordinal)
            || path.EndsWith("/tv/popular", StringComparison.Ordinal))
        {
            return Json("""{"results":[]}""");
        }

        if (path.EndsWith("/search/multi", StringComparison.Ordinal))
        {
            return Json("""
                {"page":1,"total_pages":1,"results":[
                  {"media_type":"movie","id":10331,"title":"Night of the Living Dead","release_date":"1968-10-01"},
                  {"media_type":"person","id":7,"name":"George A. Romero"}
                ]}
                """);
        }

        if (path.EndsWith("/movie/10331", StringComparison.Ordinal))
        {
            return Json("""
                {"id":10331,"title":"Night of the Living Dead","release_date":"1968-10-01","runtime":96,
                 "overview":"Dead walk.","vote_average":7.6,"genres":[{"name":"Horror"}],
                 "poster_path":"/poster.jpg","backdrop_path":"/back.jpg"}
                """);
        }

        if (path.EndsWith("/movie/10331/external_ids", StringComparison.Ordinal))
            return Json("""{"imdb_id":"tt0063350"}""");

        if (path.EndsWith("/tv/1930", StringComparison.Ordinal))
        {
            return Json("""
                {"id":1930,"name":"The Beverly Hillbillies","first_air_date":"1962-09-26","vote_average":6.8,
                 "episode_run_time":[25],"overview":"Oil!","genres":[{"name":"Comedy"}],"poster_path":null,"backdrop_path":null,
                 "seasons":[{"season_number":0,"name":"Specials","episode_count":2},
                            {"season_number":1,"name":"Season 1","episode_count":36}]}
                """);
        }

        if (path.EndsWith("/tv/1930/external_ids", StringComparison.Ordinal))
            return Json("""{"imdb_id":"tt0055662","tvdb_id":71471}""");

        if (path.EndsWith("/tv/1930/season/1", StringComparison.Ordinal))
        {
            return Json("""
                {"episodes":[
                  {"season_number":1,"episode_number":1,"name":"The Clampetts Strike Oil","air_date":"1962-09-26","runtime":25,"still_path":"/still.jpg","overview":"Oil!"},
                  {"season_number":1,"episode_number":2,"name":"Getting Settled","air_date":"1962-10-03","runtime":25,"still_path":null,"overview":null}
                ]}
                """);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Admin failures are emitted as problem details, not the legacy envelope.</summary>
    private static string? ProblemDetail(JsonDocument document)
        => document.RootElement.GetProperty("detail").GetString();

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingFFmpegRunner : IFFmpegRunner
    {
        public Task<TimeSpan?> ProbeDurationAsync(string sourceUri, CancellationToken ct)
            => Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(12.5));

        public async Task TranscodeRangeAsync(
            string sourceUri,
            TimeSpan start,
            int segmentCount,
            string outputDirectory,
            Action<int, string> onSegmentPublished,
            CancellationToken ct)
        {
            Directory.CreateDirectory(outputDirectory);
            for (var index = 0; index < segmentCount; index++)
            {
                var path = Path.Join(outputDirectory, $"{index}.ts");
                await File.WriteAllBytesAsync(path, new byte[2048], ct);
                onSegmentPublished(index, path);
            }
        }
    }
}
