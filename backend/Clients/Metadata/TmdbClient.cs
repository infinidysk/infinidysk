using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Catalog;

namespace NzbWebDAV.Clients.Metadata;

/// <summary>
/// TMDB v3 read client. Only the catalog's read surface is implemented: the four home
/// rows, multi-search, movie/series details, external identifiers, and the season
/// episode fallback used when TVDB has no mapping.
/// </summary>
public sealed class TmdbClient(IHttpClientFactory httpClientFactory, ConfigManager configManager)
{
    private const string ApiBase = "https://api.themoviedb.org/3";
    private const string PosterSize = "w500";
    private const string BackdropSize = "w1280";

    public async Task<IReadOnlyList<CatalogSummary>> GetTrendingAsync(CancellationToken ct)
    {
        using var document = await GetAsync("/trending/all/week?language=en-US", ct).ConfigureAwait(false);
        return ReadSummaries(document.RootElement, forcedType: null);
    }

    public async Task<IReadOnlyList<CatalogSummary>> GetPopularMoviesAsync(CancellationToken ct)
    {
        using var document = await GetAsync("/movie/popular?language=en-US&region=US&page=1", ct)
            .ConfigureAwait(false);
        return ReadSummaries(document.RootElement, forcedType: "movie");
    }

    public async Task<IReadOnlyList<CatalogSummary>> GetPopularSeriesAsync(CancellationToken ct)
    {
        using var document = await GetAsync("/tv/popular?language=en-US&page=1", ct).ConfigureAwait(false);
        return ReadSummaries(document.RootElement, forcedType: "series");
    }

    public async Task<IReadOnlyList<CatalogSummary>> GetTopRatedMoviesAsync(CancellationToken ct)
    {
        using var document = await GetAsync("/movie/top_rated?language=en-US&region=US&page=1", ct)
            .ConfigureAwait(false);
        return ReadSummaries(document.RootElement, forcedType: "movie");
    }

    public async Task<CatalogSearchPage> SearchMultiAsync(string query, int page, CancellationToken ct)
    {
        var url = FormattableString.Invariant(
            $"/search/multi?query={Uri.EscapeDataString(query)}&language=en-US&page={page}&include_adult=false");
        using var document = await GetAsync(url, ct).ConfigureAwait(false);
        var root = document.RootElement;
        var totalPages = root.ReadInt("total_pages") ?? 0;
        var currentPage = root.ReadInt("page") ?? page;
        return new CatalogSearchPage(
            currentPage,
            totalPages,
            currentPage < totalPages,
            ReadSummaries(root, forcedType: null));
    }

    public async Task<TmdbMovieDetails?> GetMovieAsync(int tmdbId, CancellationToken ct)
    {
        using var document = await TryGetAsync(
            FormattableString.Invariant($"/movie/{tmdbId}?language=en-US"), ct).ConfigureAwait(false);
        if (document is null) return null;
        var root = document.RootElement;
        var summary = ReadDetailsSummary(root, "movie", tmdbId);
        if (summary is null) return null;
        return new TmdbMovieDetails(summary, ReadPositiveInt(root, "runtime"), ReadGenres(root));
    }

    public async Task<TmdbSeriesDetails?> GetSeriesAsync(int tmdbId, CancellationToken ct)
    {
        using var document = await TryGetAsync(
            FormattableString.Invariant($"/tv/{tmdbId}?language=en-US"), ct).ConfigureAwait(false);
        if (document is null) return null;
        var root = document.RootElement;
        var summary = ReadDetailsSummary(root, "series", tmdbId);
        if (summary is null) return null;
        return new TmdbSeriesDetails(
            summary,
            ReadFirstArrayInt(root, "episode_run_time") ?? ReadPositiveInt(root, "runtime"),
            ReadGenres(root),
            ReadSeasons(root));
    }

    /// <summary>Movie IMDb identifier, or null when TMDB has no mapping.</summary>
    public async Task<string?> GetMovieImdbIdAsync(int tmdbId, CancellationToken ct)
    {
        using var document = await TryGetAsync(
            FormattableString.Invariant($"/movie/{tmdbId}/external_ids"), ct).ConfigureAwait(false);
        return document is null ? null : NormalizeImdbId(document.RootElement.ReadString("imdb_id"));
    }

    /// <summary>Series IMDb and TVDB identifiers, whichever TMDB knows.</summary>
    public async Task<(string? ImdbId, int? TvdbId)> GetSeriesExternalIdsAsync(int tmdbId, CancellationToken ct)
    {
        using var document = await TryGetAsync(
            FormattableString.Invariant($"/tv/{tmdbId}/external_ids"), ct).ConfigureAwait(false);
        if (document is null) return (null, null);
        var root = document.RootElement;
        var tvdbId = root.ReadInt("tvdb_id");
        return (NormalizeImdbId(root.ReadString("imdb_id")), tvdbId is > 0 ? tvdbId : null);
    }

    /// <summary>Season episodes from TMDB, used when TVDB has no mapping or fails.</summary>
    public async Task<IReadOnlyList<CatalogEpisode>> GetSeasonEpisodesAsync(
        int tmdbId, int season, CancellationToken ct)
    {
        using var document = await GetAsync(
            FormattableString.Invariant($"/tv/{tmdbId}/season/{season}?language=en-US"), ct).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("episodes", out var episodes)
            || episodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<CatalogEpisode>();
        foreach (var episode in episodes.EnumerateArray())
        {
            var number = episode.ReadInt("episode_number");
            if (number is not > 0) continue;
            items.Add(new CatalogEpisode(
                episode.ReadInt("season_number") ?? season,
                number.Value,
                episode.ReadString("name") ?? FormattableString.Invariant($"Episode {number.Value}"),
                episode.ReadString("overview"),
                episode.ReadString("air_date"),
                ReadPositiveInt(episode, "runtime"),
                MetadataHttp.BuildTmdbImageUrl(episode.ReadString("still_path"), PosterSize)));
        }

        return items;
    }

    private async Task<JsonDocument> GetAsync(string relativeUrl, CancellationToken ct)
        => await SendAsync(relativeUrl, allowNotFound: false, ct).ConfigureAwait(false)
           ?? throw CatalogException.NotFound();

    private async Task<JsonDocument?> TryGetAsync(string relativeUrl, CancellationToken ct)
        => await SendAsync(relativeUrl, allowNotFound: true, ct).ConfigureAwait(false);

    private async Task<JsonDocument?> SendAsync(string relativeUrl, bool allowNotFound, CancellationToken ct)
    {
        var token = configManager.GetTmdbReadToken();
        if (token is null)
            throw CatalogException.NotConfiguredProvider("Catalog metadata is not configured.");

        var client = httpClientFactory.CreateClient(MetadataHttp.TmdbClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        return await MetadataHttp.SendAsync(client, request, "TMDB", allowNotFound, ct).ConfigureAwait(false);
    }

    private static List<CatalogSummary> ReadSummaries(JsonElement root, string? forcedType)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<CatalogSummary>();
        foreach (var item in results.EnumerateArray())
        {
            var summary = ReadSummary(item, forcedType);
            if (summary is not null) items.Add(summary);
        }

        return items;
    }

    /// <summary>
    /// Maps one list entry. <paramref name="forcedType"/> is null for mixed feeds
    /// (trending, multi-search), where people and other media types are discarded.
    /// </summary>
    private static CatalogSummary? ReadSummary(JsonElement item, string? forcedType)
    {
        var type = forcedType ?? MapMediaType(item.ReadString("media_type"));
        if (type is null) return null;

        var id = item.ReadInt("id");
        if (id is not > 0) return null;

        var summary = ReadDetailsSummary(item, type, id.Value);
        return summary;
    }

    private static CatalogSummary? ReadDetailsSummary(JsonElement element, string type, int tmdbId)
    {
        var isMovie = type == "movie";
        var title = isMovie ? element.ReadString("title") : element.ReadString("name");
        if (title is null) return null;
        var date = isMovie ? element.ReadString("release_date") : element.ReadString("first_air_date");
        return new CatalogSummary(
            type,
            tmdbId,
            title,
            ReadYear(date),
            element.ReadString("overview"),
            MetadataHttp.NormalizeRating(element.ReadDouble("vote_average")),
            MetadataHttp.BuildTmdbImageUrl(element.ReadString("poster_path"), PosterSize),
            MetadataHttp.BuildTmdbImageUrl(element.ReadString("backdrop_path"), BackdropSize));
    }

    private static string? MapMediaType(string? mediaType) => mediaType switch
    {
        "movie" => "movie",
        "tv" => "series",
        _ => null,
    };

    private static List<string> ReadGenres(JsonElement root)
    {
        if (!root.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
            return [];

        var names = new List<string>();
        foreach (var genre in genres.EnumerateArray())
        {
            var name = genre.ReadString("name");
            if (name is not null) names.Add(name);
        }

        return names;
    }

    private static List<CatalogSeason> ReadSeasons(JsonElement root)
    {
        if (!root.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<CatalogSeason>();
        foreach (var season in seasons.EnumerateArray())
        {
            var number = season.ReadInt("season_number");
            if (number is null) continue;
            items.Add(new CatalogSeason(
                number.Value,
                season.ReadString("name") ?? FormattableString.Invariant($"Season {number.Value}"),
                Math.Max(0, season.ReadInt("episode_count") ?? 0)));
        }

        return items;
    }

    private static int? ReadPositiveInt(JsonElement element, string property)
        => element.ReadInt(property) is > 0 and var value ? value : null;

    private static int? ReadFirstArrayInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Number
                && entry.TryGetInt32(out var value)
                && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static int? ReadYear(string? date)
    {
        if (date is not { Length: >= 4 }) return null;
        return int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
               && year > 1800
            ? year
            : null;
    }

    /// <summary>Canonical <c>tt</c> identifier, or null when the provider value is unusable.</summary>
    internal static string? NormalizeImdbId(string? imdbId)
    {
        if (imdbId is not { Length: > 2 }) return null;
        if (!imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return null;
        for (var i = 2; i < imdbId.Length; i++)
        {
            if (!char.IsAsciiDigit(imdbId[i])) return null;
        }

        return "tt" + imdbId[2..];
    }
}

public sealed record TmdbMovieDetails(
    CatalogSummary Summary,
    int? RuntimeMinutes,
    IReadOnlyList<string> Genres);

public sealed record TmdbSeriesDetails(
    CatalogSummary Summary,
    int? RuntimeMinutes,
    IReadOnlyList<string> Genres,
    IReadOnlyList<CatalogSeason> Seasons);
