using NzbWebDAV.Config;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

/// <summary>
/// Resolves a Jellyfin PlaybackProgress webhook's (series, season, episode) to the
/// DavItem of the *next* episode, via Sonarr. Jellyfin's webhook payload carries no
/// file path and no series-level external id (its Provider_* fields are episode-level),
/// so this bridges the two systems purely by series title, then by season/episode
/// number once the matching Sonarr series is found.
/// </summary>
public class EpisodeResolverService(ConfigManager configManager)
{
    public async Task<Guid?> ResolveNextEpisodeDavItemIdAsync
    (
        string seriesName,
        int seasonNumber,
        int episodeNumber,
        CancellationToken ct = default
    )
    {
        // OrganizedLinksUtil requires an organized media library to search through;
        // without one there's no symlink/strm to map a Sonarr file path back to a DavItem.
        if (configManager.GetLibraryDir() is null) return null;

        foreach (var sonarrClient in configManager.GetArrConfig().GetSonarrClients())
        {
            ct.ThrowIfCancellationRequested();

            var series = await sonarrClient.FindSeriesByTitle(seriesName).ConfigureAwait(false);
            if (series is null) continue;

            var nextEpisodePath = await sonarrClient
                .GetNextEpisodeFilePath(series.Id, seasonNumber, episodeNumber)
                .ConfigureAwait(false);
            if (nextEpisodePath is null) continue;

            var davItemId = OrganizedLinksUtil.GetLibraryDavItemLinks(configManager)
                .Where(x => x.LinkPath == nextEpisodePath)
                .Select(x => (Guid?)x.DavItemId)
                .FirstOrDefault();
            if (davItemId != null) return davItemId;
        }

        return null;
    }
}
