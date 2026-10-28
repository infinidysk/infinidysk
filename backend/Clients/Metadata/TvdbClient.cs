using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Catalog;

namespace NzbWebDAV.Clients.Metadata;

/// <summary>
/// TVDB v4 read client for series episodes. Authenticates lazily with the configured API
/// key, keeps the bearer token for 27 days, and refreshes exactly once when a request is
/// rejected. Neither the API key nor the bearer token is ever logged.
/// </summary>
public sealed class TvdbClient(IHttpClientFactory httpClientFactory, ConfigManager configManager)
{
    private const string ApiBase = "https://api4.thetvdb.com/v4";
    private const int MaxEpisodePages = 20;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(27);

    private readonly Lock _gate = new();
    private Task<string>? _loginTask;
    private DateTimeOffset _loginExpiresAt;

    /// <summary>False when no TVDB API key is configured; episodes then use the TMDB fallback.</summary>
    public bool IsConfigured => configManager.GetTvdbApiKey() is not null;

    public async Task<IReadOnlyList<CatalogEpisode>> GetSeasonEpisodesAsync(
        int tvdbId, int season, CancellationToken ct)
    {
        var episodes = new List<CatalogEpisode>();
        // TVDB repeats episodes across pages; the same episode id must appear once.
        var seen = new HashSet<int>();

        for (var page = 0; ; page++)
        {
            if (page >= MaxEpisodePages)
                throw CatalogException.Provider("The TVDB episode list is too large to read.");

            var url = FormattableString.Invariant(
                $"/series/{tvdbId}/episodes/default?season={season}&page={page}");
            using var document = await GetAsync(url, ct).ConfigureAwait(false);
            var root = document.RootElement;
            AppendEpisodes(root, season, seen, episodes);
            if (!HasNextPage(root)) break;
        }

        return episodes;
    }

    private static void AppendEpisodes(
        JsonElement root,
        int season,
        HashSet<int> seen,
        List<CatalogEpisode> episodes)
    {
        if (!root.TryGetProperty("data", out var data)) return;
        JsonElement items;
        if (data.ValueKind == JsonValueKind.Array)
            items = data;
        else if (!data.TryGetProperty("episodes", out items) || items.ValueKind != JsonValueKind.Array)
            return;

        foreach (var episode in items.EnumerateArray())
        {
            var id = episode.ReadInt("id");
            if (id is null || !seen.Add(id.Value)) continue;
            // Episode zero and negative numbers are unplayable through the profile
            // search coordinates, so they are not offered as catalog episodes.
            var number = episode.ReadInt("number");
            if (number is not > 0) continue;

            var seasonNumber = episode.ReadInt("seasonNumber") ?? season;
            episodes.Add(new CatalogEpisode(
                seasonNumber,
                number.Value,
                episode.ReadString("name") ?? FormattableString.Invariant($"Episode {number.Value}"),
                episode.ReadString("overview"),
                episode.ReadString("aired"),
                episode.ReadInt("runtime") is > 0 and var runtime ? runtime : null,
                episode.ReadString("image")));
        }
    }

    /// <summary>
    /// Pagination follows only the presence of <c>links.next</c>: the provider-supplied
    /// absolute URI is never requested, so no response can redirect this client elsewhere.
    /// </summary>
    private static bool HasNextPage(JsonElement root)
    {
        return root.TryGetProperty("links", out var links)
               && links.ValueKind == JsonValueKind.Object
               && links.TryGetProperty("next", out var next)
               && next.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    private async Task<JsonDocument> GetAsync(string relativeUrl, CancellationToken ct)
    {
        var token = await GetTokenAsync(forceRefresh: false).WaitAsync(ct).ConfigureAwait(false);
        var document = await SendOnceAsync(relativeUrl, token, allowUnauthorized: true, ct).ConfigureAwait(false);
        if (document is not null) return document;

        // Tokens can be revoked before their cached lifetime ends: re-authenticate once.
        var refreshed = await GetTokenAsync(forceRefresh: true).WaitAsync(ct).ConfigureAwait(false);
        return await SendOnceAsync(relativeUrl, refreshed, allowUnauthorized: false, ct).ConfigureAwait(false)
               ?? throw CatalogException.Provider("The TVDB credentials were rejected.");
    }

    private async Task<JsonDocument?> SendOnceAsync(
        string relativeUrl,
        string token,
        bool allowUnauthorized,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(MetadataHttp.TvdbClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");

        if (!allowUnauthorized)
            return await MetadataHttp.SendAsync(client, request, "TVDB", allowNotFound: false, ct)
                .ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException)
        {
            throw CatalogException.Provider("The TVDB service is unavailable.", MetadataHttp.DefaultRetryAfter);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return null;
            return await MetadataHttp.ReadJsonAsync(response, "TVDB", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Single-flight login: concurrent catalog requests share one credential exchange, and
    /// a failed login is forgotten so the next request retries instead of replaying it.
    /// </summary>
    private Task<string> GetTokenAsync(bool forceRefresh)
    {
        lock (_gate)
        {
            if (!forceRefresh && _loginTask is { } live && _loginExpiresAt > DateTimeOffset.UtcNow)
                return live;

            var login = LoginAsync();
            _loginTask = login;
            _loginExpiresAt = DateTimeOffset.UtcNow.Add(TokenLifetime);
            ForgetOnFailure(login);
            return login;
        }
    }

    private void ForgetOnFailure(Task<string> login)
    {
        _ = login.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (_gate)
                {
                    if (ReferenceEquals(_loginTask, completed))
                        _loginTask = null;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<string> LoginAsync()
    {
        var apiKey = configManager.GetTvdbApiKey()
                     ?? throw CatalogException.NotConfiguredProvider("Catalog metadata is not configured.");

        var client = httpClientFactory.CreateClient(MetadataHttp.TvdbClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/login");
        request.Content = JsonContent.Create(new { apikey = apiKey });
        request.Headers.Accept.ParseAdd("application/json");

        var document = await MetadataHttp
            .SendAsync(client, request, "TVDB", allowNotFound: false, CancellationToken.None)
            .ConfigureAwait(false);
        if (document is null)
            throw CatalogException.Provider("The TVDB credentials were rejected.");

        using (document)
        {
            var token = document.RootElement.TryGetProperty("data", out var data)
                ? data.ReadString("token")
                : null;
            return token ?? throw CatalogException.Provider("The TVDB credentials were rejected.");
        }
    }
}
