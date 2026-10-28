namespace NzbWebDAV.Models.Catalog;

/// <summary>The four home rows, in display order.</summary>
public sealed record CatalogHome(IReadOnlyList<CatalogRow> Rows);

public sealed record CatalogRow(string Id, string Title, IReadOnlyList<CatalogSummary> Items);

/// <summary>Poster-card summary. <c>Type</c> is <c>movie</c> or <c>series</c>.</summary>
public sealed record CatalogSummary(
    string Type,
    int TmdbId,
    string Title,
    int? Year,
    string? Overview,
    double? Rating,
    string? PosterUrl,
    string? BackdropUrl);

public sealed record CatalogSearchPage(
    int Page,
    int TotalPages,
    bool HasMore,
    IReadOnlyList<CatalogSummary> Results);

public sealed record CatalogTitleDetails(
    string Type,
    int TmdbId,
    string Title,
    int? Year,
    string? Overview,
    double? Rating,
    int? RuntimeMinutes,
    IReadOnlyList<string> Genres,
    string? PosterUrl,
    string? BackdropUrl,
    IReadOnlyList<CatalogSeason> Seasons,
    IReadOnlyList<CatalogEpisode> Episodes,
    int? SelectedSeason);

public sealed record CatalogSeason(int SeasonNumber, string Name, int EpisodeCount);

public sealed record CatalogEpisode(
    int SeasonNumber,
    int EpisodeNumber,
    string Name,
    string? Overview,
    string? AirDate,
    int? RuntimeMinutes,
    string? StillUrl);

/// <summary>Provider-neutral identity handed to the existing profile search flow.</summary>
public sealed record PlaybackIdentity(string Type, string Id, string Title);

/// <summary>Result of submitting a catalog playback request through the profile search flow.</summary>
public sealed record CatalogPlayback(
    string PlaybackId,
    string DirectUrl,
    string HlsUrl,
    string ReleaseTitle);

public sealed record CreatePlaybackRequest(string Type, int TmdbId, int? Season, int? Episode);
