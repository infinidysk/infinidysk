
using NzbWebDAV.Config;
using NzbWebDAV.Models.Playback;

namespace NzbWebDAV.Clients.MediaServers;

public interface IMediaPlaybackSessionSource
{
    MediaServerType ServerType { get; }
    Task<IReadOnlyList<PlaybackObservation>> GetSessionsAsync(
        MediaServerInstance instance,
        CancellationToken cancellationToken);
}
