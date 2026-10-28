using NzbWebDAV.Models.Catalog;

namespace NzbWebDAV.Api.Controllers.Catalog;

public sealed class CatalogHomeResponse : BaseApiResponse
{
    public List<CatalogRowDto> Rows { get; set; } = [];
}

public sealed class CatalogRowDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<CatalogSummaryDto> Items { get; set; } = [];
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

    private static CatalogRowDto ToRow(CatalogRow row) => new()
    {
        Id = row.Id,
        Title = row.Title,
        Items = row.Items.Select(ToSummary).ToList(),
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
