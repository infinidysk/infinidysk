namespace NzbWebDAV.Models.Catalog;

/// <summary>The home rows, in display order.</summary>
public sealed record CatalogHome(IReadOnlyList<CatalogRow> Rows);

/// <summary>
/// One home row. Exactly one of <paramref name="Items"/> and <paramref name="People"/> is
/// populated: provider lists and the Continue Watching row carry items, the trending
/// people row carries people.
/// </summary>
public sealed record CatalogRow(
    string Id,
    string Title,
    IReadOnlyList<CatalogSummary> Items,
    IReadOnlyList<CatalogPersonSummary>? People = null);

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

/// <summary>Person card. Identity is the TMDB person id; no external ids on the wire.</summary>
public sealed record CatalogPersonSummary(string PersonId, string Name, string? ProfileUrl);

/// <summary>Person page payload: biography header plus one merged credit list.</summary>
public sealed record CatalogPersonDetails(
    string PersonId,
    string Name,
    string? ProfileUrl,
    string? Biography,
    string? Birthday,
    string? PlaceOfBirth,
    string? KnownForDepartment,
    IReadOnlyList<CatalogSummary> Credits);

/// <summary>Raw TMDB trending-person entry, including the "known for" titles.</summary>
public sealed record TmdbPerson(
    int PersonId,
    string Name,
    string? ProfileUrl,
    double Popularity,
    string? KnownFor);

/// <summary>Server-resolved display fields for a watch-history row.</summary>
public sealed record CatalogHistoryDisplay(
    string Title,
    string? Subtitle,
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

/// <summary>
/// Provider-neutral identity handed to the existing profile search flow. The display
/// fields are server-resolved so watch-history rows never take browser-supplied text.
/// </summary>
public sealed record PlaybackIdentity(
    string Type,
    string Id,
    string Title,
    string? Subtitle,
    string? PosterUrl,
    string? BackdropUrl);

/// <summary>Result of submitting a catalog playback request through the profile search flow.</summary>
public sealed record CatalogPlayback(
    string PlaybackId,
    string DirectUrl,
    string HlsUrl,
    string ReleaseTitle);

public sealed record CreatePlaybackRequest(string Type, int TmdbId, int? Season, int? Episode);
