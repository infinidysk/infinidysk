using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Catalog;

namespace NzbWebDAV.Api.Controllers.Catalog;

public sealed class CatalogHomeResponse : BaseApiResponse
{
    public List<CatalogRowDto> Rows { get; set; } = [];
}

/// <summary>
/// One home row. Exactly one of <see cref="Items"/> and <see cref="People"/> is a list;
/// the other is null, because the trending-people row is not a title list.
/// </summary>
public sealed class CatalogRowDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<CatalogSummaryDto>? Items { get; set; }
    public List<CatalogPersonDto>? People { get; set; }
}

public sealed class CatalogPersonDto
{
    public string PersonId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ProfileUrl { get; set; }
}

public sealed class CatalogSummaryDto
{
    /// <summary><c>movie</c> or <c>series</c>.</summary>
    public string Type { get; set; } = "";
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public double? Rating { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
}

public sealed class CatalogSearchResponse : BaseApiResponse
{
    public int Page { get; set; }
    public int TotalPages { get; set; }
    public bool HasMore { get; set; }
    public List<CatalogSummaryDto> Results { get; set; } = [];
}

public sealed class CatalogTitleResponse : BaseApiResponse
{
    public CatalogTitleDto? Title { get; set; }
    public List<CatalogSeasonDto> Seasons { get; set; } = [];
    public List<CatalogEpisodeDto> Episodes { get; set; } = [];
    public int? SelectedSeason { get; set; }
}

public sealed class CatalogTitleDto
{
    public string Type { get; set; } = "";
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public double? Rating { get; set; }
    public int? RuntimeMinutes { get; set; }
    public List<string> Genres { get; set; } = [];
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
}

public sealed class CatalogSeasonDto
{
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = "";
    public int EpisodeCount { get; set; }
}

public sealed class CatalogEpisodeDto
{
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string Name { get; set; } = "";
    public string? Overview { get; set; }
    public string? AirDate { get; set; }
    public int? RuntimeMinutes { get; set; }
    public string? StillUrl { get; set; }
}

public sealed class CatalogPlaybackResponse : BaseApiResponse
{
    public string PlaybackId { get; set; } = "";
    public string DirectUrl { get; set; } = "";
    public string HlsUrl { get; set; } = "";
    public string ReleaseTitle { get; set; } = "";
}

public sealed class CatalogPersonResponse : BaseApiResponse
{
    public CatalogPersonDetailsDto? Person { get; set; }
    public List<CatalogSummaryDto> Credits { get; set; } = [];
}

public sealed class CatalogPersonDetailsDto
{
    public string PersonId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ProfileUrl { get; set; }
    public string? Biography { get; set; }
    public string? Birthday { get; set; }
    public string? PlaceOfBirth { get; set; }
    public string? KnownForDepartment { get; set; }
}

public sealed class CatalogHistoryResponse : BaseApiResponse
{
    public List<CatalogHistoryItemDto> Items { get; set; } = [];
}

/// <summary>
/// One watch-history row. <see cref="Type"/> is <c>movie</c> or <c>series</c>, matching the
/// title routes the resume link targets.
/// </summary>
public sealed class CatalogHistoryItemDto
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public int TmdbId { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? PosterUrl { get; set; }
    public long PositionSeconds { get; set; }
    public long DurationSeconds { get; set; }
    public long UpdatedAtEpoch { get; set; }
    public bool Finished { get; set; }
}

/// <summary>Acknowledges a progress write; the response intentionally carries no payload.</summary>
public sealed class CatalogHistoryProgressResponse : BaseApiResponse;

/// <summary>
/// Maps the service's catalog records onto the wire DTOs. Services never reference
/// <c>NzbWebDAV.Api.Controllers</c>, so the translation lives here.
/// </summary>
internal static class CatalogContractMapper
{
    internal static CatalogHomeResponse ToResponse(CatalogHome home)
        => new() { Rows = home.Rows.Select(ToRow).ToList() };

    internal static CatalogSearchResponse ToResponse(CatalogSearchPage page) => new()
    {
        Page = page.Page,
        TotalPages = page.TotalPages,
        HasMore = page.HasMore,
        Results = page.Results.Select(ToSummary).ToList(),
    };

    internal static CatalogTitleResponse ToResponse(CatalogTitleDetails details) => new()
    {
        Title = new CatalogTitleDto
        {
            Type = details.Type,
            TmdbId = details.TmdbId,
            Title = details.Title,
            Year = details.Year,
            Overview = details.Overview,
            Rating = details.Rating,
            RuntimeMinutes = details.RuntimeMinutes,
            Genres = [.. details.Genres],
            PosterUrl = details.PosterUrl,
            BackdropUrl = details.BackdropUrl,
        },
        Seasons = details.Seasons.Select(season => new CatalogSeasonDto
        {
            SeasonNumber = season.SeasonNumber,
            Name = season.Name,
            EpisodeCount = season.EpisodeCount,
        }).ToList(),
        Episodes = details.Episodes.Select(episode => new CatalogEpisodeDto
        {
            SeasonNumber = episode.SeasonNumber,
            EpisodeNumber = episode.EpisodeNumber,
            Name = episode.Name,
            Overview = episode.Overview,
            AirDate = episode.AirDate,
            RuntimeMinutes = episode.RuntimeMinutes,
            StillUrl = episode.StillUrl,
        }).ToList(),
        SelectedSeason = details.SelectedSeason,
    };

    internal static CatalogPersonResponse ToResponse(CatalogPersonDetails person) => new()
    {
        Person = new CatalogPersonDetailsDto
        {
            PersonId = person.PersonId,
            Name = person.Name,
            ProfileUrl = person.ProfileUrl,
            Biography = person.Biography,
            Birthday = person.Birthday,
            PlaceOfBirth = person.PlaceOfBirth,
            KnownForDepartment = person.KnownForDepartment,
        },
        Credits = person.Credits.Select(ToSummary).ToList(),
    };

    internal static CatalogHistoryItemDto ToHistoryItem(CatalogWatchHistory entry) => new()
    {
        Id = entry.Id,
        Type = entry.Kind == WatchKind.Movie ? "movie" : "series",
        TmdbId = entry.TmdbId,
        Season = entry.Season,
        Episode = entry.Episode,
        Title = entry.Title,
        Subtitle = entry.Subtitle,
        PosterUrl = entry.PosterUrl,
        PositionSeconds = entry.PositionSeconds,
        DurationSeconds = entry.DurationSeconds,
        UpdatedAtEpoch = entry.UpdatedAtEpoch,
        Finished = entry.Finished,
    };

    private static CatalogRowDto ToRow(CatalogRow row) => new()
    {
        Id = row.Id,
        Title = row.Title,
        Items = row.People is null ? row.Items.Select(ToSummary).ToList() : null,
        People = row.People?.Select(ToPerson).ToList(),
    };

    private static CatalogPersonDto ToPerson(CatalogPersonSummary person) => new()
    {
        PersonId = person.PersonId,
        Name = person.Name,
        ProfileUrl = person.ProfileUrl,
    };

    private static CatalogSummaryDto ToSummary(CatalogSummary summary) => new()
    {
        Type = summary.Type,
        TmdbId = summary.TmdbId,
        Title = summary.Title,
        Year = summary.Year,
        Overview = summary.Overview,
        Rating = summary.Rating,
        PosterUrl = summary.PosterUrl,
        BackdropUrl = summary.BackdropUrl,
    };
}
