
using System.Net.Http.Headers;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Models.Playback;

namespace NzbWebDAV.Clients.MediaServers;

public sealed class EmbyPlaybackSessionSource : EmbyJellyfinPlaybackSessionSource
{
    public EmbyPlaybackSessionSource() : base(MediaServerType.Emby) { }
    internal EmbyPlaybackSessionSource(HttpClient client) : base(MediaServerType.Emby, client) { }
}

public sealed class JellyfinPlaybackSessionSource : EmbyJellyfinPlaybackSessionSource
{
    public JellyfinPlaybackSessionSource() : base(MediaServerType.Jellyfin) { }
    internal JellyfinPlaybackSessionSource(HttpClient client) : base(MediaServerType.Jellyfin, client) { }
}

public abstract class EmbyJellyfinPlaybackSessionSource : IMediaPlaybackSessionSource
{
    private static readonly HttpClient SharedClient = new(MediaServerHttp.CreateSessionHandler());
    private readonly HttpClient _client;

    protected EmbyJellyfinPlaybackSessionSource(MediaServerType serverType) : this(serverType, SharedClient) { }
    internal EmbyJellyfinPlaybackSessionSource(MediaServerType serverType, HttpClient client)
    {
        ServerType = serverType;
        _client = client;
    }

    public MediaServerType ServerType { get; }

    public async Task<IReadOnlyList<PlaybackObservation>> GetSessionsAsync(
        MediaServerInstance instance,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, MediaServerHttp.Endpoint(instance.BaseUrl, "Sessions"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Emby-Token", instance.Token);
        using var document = await MediaServerHttp.SendJsonAsync(_client, request, cancellationToken)
            .ConfigureAwait(false);
        return Parse(document.RootElement);
    }

    internal static IReadOnlyList<PlaybackObservation> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Emby/Jellyfin session response must be a JSON array.");
        var result = new List<PlaybackObservation>();
        foreach (var session in root.EnumerateArray())
        {
            var sessionId = MediaServerJson.String(session, "Id");
            if (string.IsNullOrWhiteSpace(sessionId)
                || !MediaServerJson.TryGet(session, "NowPlayingItem", out var item)
                || item.ValueKind != JsonValueKind.Object)
                continue;

            MediaServerJson.TryGet(session, "PlayState", out var playState);
            var mediaSourceId = MediaServerJson.String(playState, "MediaSourceId");
            var itemPath = MediaServerJson.String(item, "Path");
            string? path = null;
            var candidateCount = 0;

            // PlayerStateInfo identifies the active media version. Prefer its
            // embedded MediaSource when present, then its MediaSourceId against
            // NowPlayingItem.MediaSources. Never let a generic item Path override
            // an explicit active-version identity.
            if (MediaServerJson.TryGet(playState, "MediaSource", out var activeSource)
                && activeSource.ValueKind == JsonValueKind.Object)
            {
                var activeId = MediaServerJson.String(activeSource, "Id");
                if (mediaSourceId is null
                    || activeId is null
                    || string.Equals(activeId, mediaSourceId, StringComparison.Ordinal))
                {
                    path = MediaServerJson.String(activeSource, "Path");
                    mediaSourceId ??= activeId;
                }
            }

            if (string.IsNullOrWhiteSpace(path)
                && MediaServerJson.TryGet(item, "MediaSources", out var sources)
                && sources.ValueKind == JsonValueKind.Array)
            {
                var candidates = sources.EnumerateArray()
                    .Where(source => source.ValueKind == JsonValueKind.Object)
                    .Select(source => new
                    {
                        Id = MediaServerJson.String(source, "Id"),
                        Path = MediaServerJson.String(source, "Path"),
                    })
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path))
                    .ToList();
                candidateCount = candidates.Count;

                if (mediaSourceId is not null)
                {
                    path = candidates.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, mediaSourceId, StringComparison.Ordinal))?.Path;
                }
                else if (candidates.Count == 1)
                {
                    mediaSourceId = candidates[0].Id;
                    path = candidates[0].Path;
                }
            }

            var itemId = MediaServerJson.String(item, "Id");
            if (string.IsNullOrWhiteSpace(path)
                && candidateCount == 0
                && (mediaSourceId is null
                    || string.Equals(mediaSourceId, itemId, StringComparison.Ordinal)))
            {
                path = itemPath;
            }

            var paused = MediaServerJson.Bool(playState, "IsPaused");
            result.Add(new PlaybackObservation
            {
                NativeSessionId = sessionId,
                UserName = MediaServerJson.String(session, "UserName"),
                ClientName = MediaServerJson.String(session, "Client"),
                DeviceName = MediaServerJson.String(session, "DeviceName"),
                ItemId = MediaServerJson.String(item, "Id"),
                Title = MediaServerJson.String(item, "Name"),
                MediaType = MediaServerJson.String(item, "Type"),
                SeriesName = MediaServerJson.String(item, "SeriesName"),
                SeasonNumber = MediaServerJson.Int32(item, "ParentIndexNumber"),
                EpisodeNumber = MediaServerJson.Int32(item, "IndexNumber"),
                State = paused switch
                {
                    true => PlaybackState.Paused,
                    false => PlaybackState.Playing,
                    null => PlaybackState.Unknown,
                },
                PositionMs = MediaServerJson.TicksToMilliseconds(MediaServerJson.Int64(playState, "PositionTicks")),
                DurationMs = MediaServerJson.TicksToMilliseconds(MediaServerJson.Int64(item, "RunTimeTicks")),
                DeliveryMethod = ParseMethod(MediaServerJson.String(playState, "PlayMethod")),
                MediaSourceId = mediaSourceId,
                MediaSourcePath = path,
            });
        }
        return result;
    }

    private static PlaybackDeliveryMethod ParseMethod(string? value) =>
        value?.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal)
            .ToLowerInvariant() switch
        {
            "directplay" => PlaybackDeliveryMethod.DirectPlay,
            "directstream" => PlaybackDeliveryMethod.DirectStream,
            "transcode" or "transcoding" => PlaybackDeliveryMethod.Transcode,
            _ => PlaybackDeliveryMethod.Unknown,
        };
}
