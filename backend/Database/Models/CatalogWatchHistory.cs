namespace NzbWebDAV.Database.Models;

/// <summary>
/// One resumable catalog playback, scoped to the viewer profile that owns it: the unique
/// index over (ProfileId, Kind, TmdbId, Season, Episode) where the row is unfinished is
/// the dedupe key, so a re-watch after completion starts a new row.
/// </summary>
public sealed class CatalogWatchHistory
{
    public int Id { get; set; }

    /// <summary>Viewer profile that owns this row. 1 is the seeded "Main" profile.</summary>
    public int ProfileId { get; set; }

    public WatchKind Kind { get; set; }
    public int TmdbId { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string Title { get; set; } = "";

    /// <summary>Episode label, e.g. <c>S1E5 · The Clampetts Strike Oil</c>; null for movies.</summary>
    public string? Subtitle { get; set; }

    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }

    /// <summary>Zero while the media duration is still unknown.</summary>
    public long DurationSeconds { get; set; }

    public long PositionSeconds { get; set; }

    /// <summary>Unix seconds of the first insert for this title.</summary>
    public long StartedAtEpoch { get; set; }

    /// <summary>Unix seconds of the last write; the list order and retention key.</summary>
    public long UpdatedAtEpoch { get; set; }

    /// <summary>True once the player reported the end of the media.</summary>
    public bool Finished { get; set; }
}

public enum WatchKind
{
    Movie = 0,
    Episode = 1,
}
