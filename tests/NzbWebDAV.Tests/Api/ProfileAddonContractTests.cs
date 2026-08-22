using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Api.Controllers.Profiles;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class ProfileAddonContractTests
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Manifest_ValidEnabledProfile_ReturnsContract()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var client = factory.CreateClient();
        await ConfigureAddonProfileAsync(admin, Token, "Contract Profile");

        using var response = await client.GetAsync($"/adapters/addon/{Token}/manifest.json");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.True(response.Headers.CacheControl?.NoStore);
        JsonContractValidator.AssertMatchesSchema(json.RootElement, "stremio/v1/profile-manifest.schema.json");
        Assert.Equal($"nzbdav.profile.{Token}", json.RootElement.GetProperty("id").GetString());
        Assert.Equal("Contract Profile", json.RootElement.GetProperty("name").GetString());
        Assert.Equal(ProfileAddonFactory.LogoUrl, json.RootElement.GetProperty("logo").GetString());
        Assert.Equal("0.0.0", json.RootElement.GetProperty("version").GetString());
        Assert.Contains("indexers", json.RootElement.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Manifest_FallbackName_UsesInfiniDyskBranding()
    {
        var manifest = ProfileAddonFactory.CreateManifest(
            new ProfileConfig.Profile { Token = Token, Name = "  " },
            Token);

        Assert.Equal("InfiniDysk Search Profile", manifest.Name);
        Assert.False(manifest.BehaviorHints.Configurable);
        Assert.False(manifest.BehaviorHints.ConfigurationRequired);
        Assert.Equal(["stream"], manifest.Resources);
        Assert.Equal(["movie", "series"], manifest.Types);
    }

    [Fact]
    public async Task Manifest_InvalidToken_Returns404()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/adapters/addon/missing-token/manifest.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Manifest_DisabledAddon_Returns404()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var client = factory.CreateClient();
        await ConfigureAddonProfileAsync(admin, Token, "Disabled", enabledAdapters: ["json"]);

        using var response = await client.GetAsync($"/adapters/addon/{Token}/manifest.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Manifest_Options_ReturnsCors()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Options,
            $"/adapters/addon/{Token}/manifest.json");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public void StreamFactory_EmptyCandidates_ProduceEmptyArray()
    {
        var response = ProfileAddonFactory.CreateStreamResponse(
            new SearchProfileService.SearchResult
            {
                ProfileToken = Token,
                Type = "movie",
                Id = "tt0111161",
                Candidates = [],
                PlayTokens = [],
            },
            "https://host.example",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            _ => false);

        Assert.Empty(response.Streams);
        JsonContractValidator.AssertMatchesSchema(
            JsonSerializer.SerializeToElement(response),
            "stremio/v1/profile-stream-response.schema.json");
    }

    [Fact]
    public void StreamFactory_MapsPlayUrlHintsAndOptionalMetadata()
    {
        var candidate = new NzbResolutionCache.Candidate
        {
            IndexerName = "Primary",
            SourceIndexerName = "Indexer Proxy",
            IndexerUserAgent = "test-agent",
            NzbUrl = "https://indexer.example/get/123",
            Title = "Example.Movie.2026.1080p",
            Size = 1_500_000_000,
            Posted = DateTimeOffset.UtcNow.AddDays(-2),
            Language = "English",
            Subs = "English, Spanish",
            VerifiedAvailable = true,
        };

        var stream = ProfileAddonFactory.CreateStream(
            candidate,
            "movie",
            Token,
            "play-token",
            "https://host.example/nzbdav",
            inLibrary: true,
            verifiedAvailable: true);
        var json = JsonSerializer.SerializeToElement(new ProfileAddonStreamResponse { Streams = [stream] });

        JsonContractValidator.AssertMatchesSchema(json, "stremio/v1/profile-stream-response.schema.json");
        Assert.Equal("[NZB] Indexer Proxy", stream.Name);
        Assert.Equal("https://host.example/nzbdav/adapters/addon/aaaaaaaaaaaaaaaaaaaaaaaa/play/play-token.mkv", stream.Url);
        Assert.Equal("play-token", stream.FailoverId);
        Assert.Equal("play-token", stream.Extra.FailoverId);
        Assert.Equal("Example.Movie.2026.1080p", stream.BehaviorHints.Filename);
        Assert.Equal(1_500_000_000, stream.BehaviorHints.VideoSize);
        Assert.True(stream.BehaviorHints.NotWebReady);
        Assert.Equal("Indexer Proxy", stream.Meta.Indexer);
        Assert.True(stream.Meta.InLibrary);
        Assert.Equal("available", stream.Meta.Availability);
        Assert.Equal(["en"], stream.Meta.Languages);
        Assert.Equal(["en", "es"], stream.Meta.SubtitleLanguages);
        Assert.Contains("⚡ Ready", stream.Description, StringComparison.Ordinal);
        Assert.Contains("✅ Verified", stream.Description, StringComparison.Ordinal);
        Assert.Contains("🇬🇧", stream.Description, StringComparison.Ordinal);
        Assert.Contains("💬 Subs: en, es", stream.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamFactory_MarksPreflightAvailableWithoutGuessingTimeout()
    {
        var candidate = new NzbResolutionCache.Candidate
        {
            IndexerName = "Primary",
            IndexerUserAgent = "ua",
            NzbUrl = "https://indexer.example/available",
            Title = "Example.Movie.2026.1080p",
            Size = 100,
        };

        var available = ProfileAddonFactory.CreateStreamResponse(
            Result([candidate], ["play-token"]),
            "https://host.example",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            nzbUrl => nzbUrl == "https://indexer.example/available");
        var timeout = ProfileAddonFactory.CreateStreamResponse(
            Result([candidate], ["play-token"]),
            "https://host.example",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            _ => false);

        Assert.Equal("available", available.Streams[0].Meta.Availability);
        Assert.Null(timeout.Streams[0].Meta.Availability);
    }

    [Fact]
    public void StreamFactory_OmitsUnknownOptionalMetadata()
    {
        var candidate = new NzbResolutionCache.Candidate
        {
            IndexerName = "Primary",
            IndexerUserAgent = "test-agent",
            NzbUrl = "https://indexer.example/get/123",
            Title = "Example.Movie.2026.1080p",
            Size = 100,
        };

        var stream = ProfileAddonFactory.CreateStream(
            candidate, "movie", Token, "play-token", "https://host.example",
            inLibrary: false, verifiedAvailable: false);

        Assert.Null(stream.Meta.InLibrary);
        Assert.Null(stream.Meta.Availability);
        Assert.Null(stream.Meta.Languages);
        Assert.Null(stream.Meta.SubtitleLanguages);
        Assert.DoesNotContain("⚡ Ready", stream.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("✅ Verified", stream.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("💬 Subs:", stream.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamFactory_SanitizesFilenameAndClampsNegativeVideoSize()
    {
        var candidate = new NzbResolutionCache.Candidate
        {
            IndexerName = "Primary",
            IndexerUserAgent = "test-agent",
            NzbUrl = "https://indexer.example/get/123",
            Title = "",
            Size = -1,
        };

        var stream = ProfileAddonFactory.CreateStream(
            candidate, "movie", Token, "play-token", "https://host.example",
            inLibrary: false, verifiedAvailable: false);

        Assert.Equal("untitled", stream.BehaviorHints.Filename);
        Assert.Equal(0, stream.BehaviorHints.VideoSize);
    }

    [Fact]
    public async Task Play_ExistingVideoWithForwardedPrefix_RedirectsToPrefixedView()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        await ConfigureAddonProfileAsync(admin, Token, "Redirect Profile");

        const string title = "Movie.2024.1080p";
        var historyId = Guid.NewGuid();
        await factory.SeedHistoryItemAsync(
            historyId,
            HistoryItem.DownloadStatusOption.Completed,
            $"{title}.nzb");
        await factory.AddDavItemsAsync(UsenetFile($"{title}.mkv", historyId));

        var cache = factory.Services.GetRequiredService<NzbResolutionCache>();
        var playToken = (await cache.AddGroupAsync(
            [
                new NzbResolutionCache.Candidate
                {
                    IndexerName = "Primary",
                    IndexerUserAgent = "test-agent",
                    NzbUrl = "https://indexer.example/get/123",
                    Title = title,
                    Size = 1_500_000_000,
                },
            ],
            "movie",
            Token,
            "tt0111161"))[0];

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/adapters/addon/{Token}/play/{playToken}.mkv");
        request.Headers.Add("X-Forwarded-Prefix", "/infinidysk");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/infinidysk/view/", response.Headers.Location.OriginalString);
    }

    [Fact]
    public async Task FailoverOrder_ReportsMatchedTokensForSameProfile()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var client = factory.CreateClient();
        await ConfigureAddonProfileAsync(admin, Token, "Order Profile");

        var cache = factory.Services.GetRequiredService<NzbResolutionCache>();
        var tokens = await cache.AddGroupAsync(
            [
                new NzbResolutionCache.Candidate
                {
                    IndexerName = "A",
                    IndexerUserAgent = "ua",
                    NzbUrl = "https://indexer.example/a",
                    Title = "A",
                    Size = 1,
                },
                new NzbResolutionCache.Candidate
                {
                    IndexerName = "B",
                    IndexerUserAgent = "ua",
                    NzbUrl = "https://indexer.example/b",
                    Title = "B",
                    Size = 2,
                },
            ],
            "movie",
            Token,
            "tt0111161");

        using var response = await client.PostAsJsonAsync(
            $"/adapters/addon/{Token}/failover_order",
            new { streams = new[] { new { failoverId = tokens[1] }, new { failoverId = tokens[0] } } });
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("matched").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("unmatched").GetInt32());

        var store = factory.Services.GetRequiredService<PreferredOrderStore>();
        Assert.Equal(2, store.GetOrder(Token, "movie", "tt0111161")!.Count);
    }

    [Fact]
    public async Task FailoverOrder_RejectsUnknownProfileEmptyBodyAndCrossProfileTokens()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var client = factory.CreateClient();
        const string otherToken = "bbbbbbbbbbbbbbbbbbbbbbbb";
        await ConfigureAddonProfilesAsync(admin,
            new ProfileConfig.Profile { Token = Token, Name = "Order Profile", EnabledAdapters = ["addon"] },
            new ProfileConfig.Profile { Token = otherToken, Name = "Other", EnabledAdapters = ["addon"] });

        var cache = factory.Services.GetRequiredService<NzbResolutionCache>();
        var otherTokens = await cache.AddGroupAsync(
            [
                new NzbResolutionCache.Candidate
                {
                    IndexerName = "A",
                    IndexerUserAgent = "ua",
                    NzbUrl = "https://indexer.example/other",
                    Title = "Other",
                    Size = 1,
                },
            ],
            "movie",
            otherToken,
            "tt0111161");

        using var missing = await client.PostAsJsonAsync(
            "/adapters/addon/missing-token/failover_order",
            new { streams = new[] { new { failoverId = "x" } } });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var empty = await client.PostAsJsonAsync(
            $"/adapters/addon/{Token}/failover_order",
            new { streams = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var cross = await client.PostAsJsonAsync(
            $"/adapters/addon/{Token}/failover_order",
            new { streams = new[] { new { failoverId = otherTokens[0] } } });
        using var crossJson = await JsonDocument.ParseAsync(await cross.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.OK, cross.StatusCode);
        Assert.Equal(0, crossJson.RootElement.GetProperty("matched").GetInt32());
        Assert.Equal(1, crossJson.RootElement.GetProperty("unmatched").GetInt32());

        using var optionsRequest = new HttpRequestMessage(
            HttpMethod.Options,
            $"/adapters/addon/{Token}/failover_order");
        using var options = await client.SendAsync(optionsRequest);
        Assert.Equal(HttpStatusCode.NoContent, options.StatusCode);
        Assert.Equal("*", Assert.Single(options.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    private static SearchProfileService.SearchResult Result(
        IReadOnlyList<NzbResolutionCache.Candidate> candidates,
        string[] playTokens) =>
        new()
        {
            ProfileToken = Token,
            Type = "movie",
            Id = "tt0111161",
            Candidates = candidates,
            PlayTokens = playTokens,
        };

    private static Task ConfigureAddonProfileAsync(
        HttpClient client,
        string token,
        string name,
        IReadOnlyList<string>? enabledAdapters = null) =>
        ConfigureAddonProfilesAsync(client, new ProfileConfig.Profile
        {
            Token = token,
            Name = name,
            EnabledAdapters = enabledAdapters?.ToList() ?? ["addon"],
        });

    private static DavItem UsenetFile(string name, Guid historyItemId) => DavItem.New(
        Guid.NewGuid(),
        DavItem.ContentFolder,
        name,
        fileSize: 100,
        DavItem.ItemType.UsenetFile,
        DavItem.ItemSubType.NzbFile,
        releaseDate: DateTimeOffset.UtcNow.AddDays(-1),
        lastHealthCheck: null,
        historyItemId: historyItemId,
        fileBlobId: null);

    private static async Task ConfigureAddonProfilesAsync(
        HttpClient client,
        params ProfileConfig.Profile[] profiles)
    {
        var payload = JsonSerializer.Serialize(new ProfileConfig { Profiles = [.. profiles] });
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(payload), ConfigKeys.ProfilesInstances);
        using var response = await client.PostAsync("/api/update-config", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
