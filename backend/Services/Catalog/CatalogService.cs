using System.Data.Common;
using System.Globalization;
using NzbWebDAV.Clients.Metadata;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Catalog;
using Serilog;

namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// TMDB/TVDB-backed catalog read model: the home rows (provider lists, trending people,
/// Continue Watching), multi-search, title and person details, and the provider-neutral
/// identity the existing profile search flow needs for playback. Provider responses are
/// cached with single-flight tasks so a burst of home requests issues one request per
/// provider endpoint; the local watch history is read fresh on every home request.
/// </summary>
public sealed class CatalogService(TmdbClient tmdbClient, TvdbClient tvdbClient)
{
    internal const string TrendingPeopleRowId = "trending-people";

    internal static readonly TimeSpan HomeTtl = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan TitleTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Provider rows in display order. Continue Watching is not a provider row: it is
    /// served by the watch-history endpoint, which carries the resume coordinates and
    /// progress the row renders.
    /// </summary>
    private static readonly RowDefinition[] RowDefinitions =
    [
        new("trending", "Trending Now"),
        new("popular-movies", "Popular Movies"),
        new("popular-series", "Popular Series"),
        new("top-rated-movies", "Top Rated Movies"),
        new("now-playing", "New Releases"),
        new("upcoming-movies", "Upcoming"),
        new("top-rated-series", "Top Rated TV"),
        new(TrendingPeopleRowId, "Trending People"),
    ];

    private readonly MetadataMemo _memo = new();

    public async Task<CatalogHome> GetHomeAsync(CancellationToken ct)
    {
        var rows = await Task.WhenAll(RowDefinitions.Select(row =>
                _memo.GetOrCreateAsync(RowKey(row.Id), HomeTtl, token => LoadRowAsync(row, token), ct)))
            .ConfigureAwait(false);
        return new CatalogHome(rows);
    }

    public Task<CatalogSearchPage> SearchAsync(string query, int page, CancellationToken ct)
    {
        var normalized = query.Trim();
        var key = string.Create(CultureInfo.InvariantCulture, $"search:{page}:{normalized}");
        return _memo.GetOrCreateAsync(key, SearchTtl, token => tmdbClient.SearchMultiAsync(normalized, page, token), ct);
    }

    public async Task<CatalogTitleDetails?> GetTitleAsync(
        string type, int tmdbId, int? season, CancellationToken ct)
    {
        if (IsMovie(type))
        {
            var movie = await GetMovieAsync(tmdbId, ct).ConfigureAwait(false);
            return movie?.Details;
        }

        var series = await GetSeriesAsync(tmdbId, season, ct).ConfigureAwait(false);
        return series?.Details;
    }

    /// <summary>
    /// Resolves the external identity for the existing profile search flow. Only provider
    /// identifiers are produced here: the browser never supplies external ids or NZB URLs.
    /// </summary>
    public async Task<PlaybackIdentity> GetPlaybackIdentityAsync(
        string type, int tmdbId, int? season, int? episode, CancellationToken ct)
    {
        if (IsMovie(type))
        {
            var movie = await GetMovieAsync(tmdbId, ct).ConfigureAwait(false)
                        ?? throw CatalogException.NotFound();
            var imdbId = movie.ImdbId
                         ?? throw CatalogException.UnprocessableEntity(
                             "This movie has no usable IMDb identifier.");
            return new PlaybackIdentity(
                "movie",
                imdbId,
                movie.Details.Title,
                null,
                movie.Details.PosterUrl,
                movie.Details.BackdropUrl);
        }

        var series = await GetSeriesAsync(tmdbId, season, ct).ConfigureAwait(false)
                     ?? throw CatalogException.NotFound();
        if (season is not { } requestedSeason || episode is not { } requestedEpisode)
        {
            throw CatalogException.UnprocessableEntity(
                "This series has no usable IMDb or TVDB identifier.");
        }

        var title = series.Details.Title;
        var subtitle = EpisodeLabel(series.Details, requestedSeason, requestedEpisode);
        if (series.TvdbId is { } tvdbId)
        {
            return new PlaybackIdentity(
                "series",
                FormattableString.Invariant($"tvdb:{tvdbId}:{requestedSeason}:{requestedEpisode}"),
                title,
                subtitle,
                series.Details.PosterUrl,
                series.Details.BackdropUrl);
        }

        if (series.ImdbId is { } seriesImdbId)
        {
            return new PlaybackIdentity(
                "series",
                FormattableString.Invariant($"{seriesImdbId}:{requestedSeason}:{requestedEpisode}"),
                title,
                subtitle,
                series.Details.PosterUrl,
                series.Details.BackdropUrl);
        }

        throw CatalogException.UnprocessableEntity("This series has no usable IMDb or TVDB identifier.");
    }

    private static bool IsMovie(string type) => string.Equals(type, "movie", StringComparison.Ordinal);

    private async Task<CatalogRow> LoadRowAsync(RowDefinition row, CancellationToken ct)
    {
        if (row.Id == TrendingPeopleRowId)
        {
            var people = await tmdbClient.GetTrendingPeopleAsync(ct).ConfigureAwait(false);
            return new CatalogRow(row.Id, row.Title, [], people.Select(ToPersonSummary).ToList());
        }

        var items = row.Id switch
        {
            "trending" => await tmdbClient.GetTrendingAsync(ct).ConfigureAwait(false),
            "popular-movies" => await tmdbClient.GetPopularMoviesAsync(ct).ConfigureAwait(false),
            "popular-series" => await tmdbClient.GetPopularSeriesAsync(ct).ConfigureAwait(false),
            "top-rated-movies" => await tmdbClient.GetTopRatedMoviesAsync(ct).ConfigureAwait(false),
            "now-playing" => await tmdbClient.GetNowPlayingMoviesAsync(ct).ConfigureAwait(false),
            "upcoming-movies" => await tmdbClient.GetUpcomingMoviesAsync(ct).ConfigureAwait(false),
            "top-rated-series" => await tmdbClient.GetTopRatedSeriesAsync(ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown catalog row '{row.Id}'."),
        };
        return new CatalogRow(row.Id, row.Title, items);
    }

    /// <summary>
    /// Server-side display metadata for a progress report, so the browser cannot write
    /// arbitrary titles or artwork into the watch history.
    /// </summary>
    public async Task<CatalogHistoryDisplay?> GetHistoryDisplayAsync(
        string type, int tmdbId, int? season, int? episode, CancellationToken ct)
    {
        if (IsMovie(type))
        {
            var movie = await GetMovieAsync(tmdbId, ct).ConfigureAwait(false);
            return movie is null
                ? null
                : new CatalogHistoryDisplay(
                    movie.Details.Title, null, movie.Details.PosterUrl, movie.Details.BackdropUrl);
        }

        var series = await GetSeriesAsync(tmdbId, season, ct).ConfigureAwait(false);
        if (series is null) return null;
        var subtitle = season is { } requestedSeason && episode is { } requestedEpisode
            ? EpisodeLabel(series.Details, requestedSeason, requestedEpisode)
            : null;
        return new CatalogHistoryDisplay(
            series.Details.Title, subtitle, series.Details.PosterUrl, series.Details.BackdropUrl);
    }

    /// <summary>Person details and combined credits; null when TMDB does not know the id.</summary>
    public Task<CatalogPersonDetails?> GetPersonAsync(int personId, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"person:{personId}");
        return _memo.GetOrCreateAsync<CatalogPersonDetails?>(key, TitleTtl, async token =>
        {
            var person = await tmdbClient.GetPersonAsync(personId, token).ConfigureAwait(false);
            if (person is null) return null;
            return new CatalogPersonDetails(
                person.Person.PersonId.ToString(CultureInfo.InvariantCulture),
                person.Person.Name,
                person.Person.ProfileUrl,
                person.Biography,
                person.Birthday,
                person.PlaceOfBirth,
                person.KnownForDepartment,
                person.Credits);
        }, ct);
    }

    /// <summary>`S1E5` plus the episode name when the selected season lists it.</summary>
    private static string EpisodeLabel(CatalogTitleDetails details, int season, int episode)
    {
        var label = FormattableString.Invariant($"S{season}E{episode}");
        var match = details.Episodes.FirstOrDefault(candidate =>
            candidate.SeasonNumber == season && candidate.EpisodeNumber == episode);
        return match is null ? label : FormattableString.Invariant($"{label} · {match.Name}");
    }

    private static CatalogPersonSummary ToPersonSummary(TmdbPerson person)
        => new(
            person.PersonId.ToString(CultureInfo.InvariantCulture),
            person.Name,
            person.ProfileUrl);

    private Task<MovieMetadata?> GetMovieAsync(int tmdbId, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"movie:{tmdbId}");
        return _memo.GetOrCreateAsync<MovieMetadata?>(key, TitleTtl, async token =>
        {
            var detailsTask = tmdbClient.GetMovieAsync(tmdbId, token);
            var imdbTask = tmdbClient.GetMovieImdbIdAsync(tmdbId, token);
            await Task.WhenAll(detailsTask, imdbTask).ConfigureAwait(false);

            var movie = await detailsTask.ConfigureAwait(false);
            if (movie is null) return null;

            var details = new CatalogTitleDetails(
                "movie",
                tmdbId,
                movie.Summary.Title,
                movie.Summary.Year,
                movie.Summary.Overview,
                movie.Summary.Rating,
                movie.RuntimeMinutes,
                movie.Genres,
                movie.Summary.PosterUrl,
                movie.Summary.BackdropUrl,
                [],
                [],
                null);
            return new MovieMetadata(details, await imdbTask.ConfigureAwait(false));
        }, ct);
    }

    private Task<SeriesMetadata?> GetSeriesAsync(int tmdbId, int? season, CancellationToken ct)
    {
        var seasonKey = season is { } requested
            ? requested.ToString(CultureInfo.InvariantCulture)
            : "default";
        var key = string.Create(CultureInfo.InvariantCulture, $"series:{tmdbId}:{seasonKey}");
        return _memo.GetOrCreateAsync<SeriesMetadata?>(key, TitleTtl, async token =>
        {
            var detailsTask = tmdbClient.GetSeriesAsync(tmdbId, token);
            var externalTask = GetSeriesExternalIdsAsync(tmdbId, token);
            await Task.WhenAll(detailsTask, externalTask).ConfigureAwait(false);

            var show = await detailsTask.ConfigureAwait(false);
            if (show is null) return null;

            var (imdbId, tvdbId) = await externalTask.ConfigureAwait(false);
            var selectedSeason = SelectSeason(show.Seasons, season);
            var episodes = selectedSeason is { } selected
                ? await GetSeasonEpisodesAsync(tmdbId, selected, token).ConfigureAwait(false)
                : [];

            var details = new CatalogTitleDetails(
                "series",
                tmdbId,
                show.Summary.Title,
                show.Summary.Year,
                show.Summary.Overview,
                show.Summary.Rating,
                show.RuntimeMinutes,
                show.Genres,
                show.Summary.PosterUrl,
                show.Summary.BackdropUrl,
                show.Seasons,
                episodes,
                selectedSeason);
            return new SeriesMetadata(details, imdbId, tvdbId);
        }, ct);
    }

    private Task<(string? ImdbId, int? TvdbId)> GetSeriesExternalIdsAsync(int tmdbId, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"tv-external:{tmdbId}");
        return _memo.GetOrCreateAsync(key, TitleTtl, token => tmdbClient.GetSeriesExternalIdsAsync(tmdbId, token), ct);
    }

    private Task<IReadOnlyList<CatalogEpisode>> GetSeasonEpisodesAsync(
        int tmdbId, int season, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"episodes:{tmdbId}:{season}");
        return _memo.GetOrCreateAsync<IReadOnlyList<CatalogEpisode>>(key, TitleTtl, async token =>
        {
            // A missing TVDB key skips the episode request entirely instead of failing.
            if (tvdbClient.IsConfigured)
            {
                var (_, tvdbId) = await GetSeriesExternalIdsAsync(tmdbId, token).ConfigureAwait(false);
                if (tvdbId is { } id)
                {
                    try
                    {
                        return await tvdbClient.GetSeasonEpisodesAsync(id, season, token)
                            .ConfigureAwait(false);
                    }
                    catch (CatalogException e)
                    {
                        // Credential-free: only the status code and ids are recorded.
                        Log.Warning(
                            "Catalog: TVDB episodes unavailable for TMDB series {TmdbId} season {Season} (status {Status}); using the TMDB season fallback.",
                            tmdbId, season, e.StatusCode);
                    }
                }
            }

            return await tmdbClient.GetSeasonEpisodesAsync(tmdbId, season, token).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// The requested season when the provider lists it, otherwise the first real season,
    /// falling back to season zero when specials are the only thing available.
    /// </summary>
    private static int? SelectSeason(IReadOnlyList<CatalogSeason> seasons, int? requested)
    {
        if (seasons.Count == 0) return null;
        if (requested is { } wanted)
            return seasons.Any(season => season.SeasonNumber == wanted) ? wanted : null;

        foreach (var season in seasons)
        {
            if (season.SeasonNumber != 0) return season.SeasonNumber;
        }

        return seasons[0].SeasonNumber;
    }

    private static string RowKey(string rowId) => "home:" + rowId;

    private sealed record RowDefinition(string Id, string Title);

    private sealed record MovieMetadata(CatalogTitleDetails Details, string? ImdbId);

    private sealed record SeriesMetadata(CatalogTitleDetails Details, string? ImdbId, int? TvdbId);
}
