
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Models.Playback;

namespace NzbWebDAV.Clients.MediaServers;

public sealed class PlexPlaybackSessionSource : IMediaPlaybackSessionSource
{
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    });
    private readonly HttpClient _client;

    public PlexPlaybackSessionSource() : this(SharedClient) { }
    internal PlexPlaybackSessionSource(HttpClient client) => _client = client;

    public MediaServerType ServerType => MediaServerType.Plex;

    public async Task<IReadOnlyList<PlaybackObservation>> GetSessionsAsync(
        MediaServerInstance instance,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, MediaServerHttp.Endpoint(instance.BaseUrl, "status/sessions"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Plex-Token", instance.Token);
        using var document = await MediaServerHttp.SendJsonAsync(_client, request, cancellationToken)
            .ConfigureAwait(false);
        return Parse(document.RootElement);
    }

    internal static IReadOnlyList<PlaybackObservation> Parse(JsonElement root)
    {
        var container = root;
        if (MediaServerJson.TryGet(root, "MediaContainer", out var wrapped))
            container = wrapped;
        if (!MediaServerJson.TryGet(container, "Metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<PlaybackObservation>();
        foreach (var item in metadata.EnumerateArray())
        {
            if (!MediaServerJson.TryGet(item, "Session", out var session)) continue;
            var sessionId = MediaServerJson.String(session, "id");
            if (string.IsNullOrWhiteSpace(sessionId)) continue;

            MediaServerJson.TryGet(item, "Player", out var player);
            MediaServerJson.TryGet(item, "User", out var user);
            var state = ParseState(MediaServerJson.String(player, "state"));

            string? sourcePath = null;
            string? sourceId = null;
            var method = PlaybackDeliveryMethod.Unknown;
            if (MediaServerJson.TryGet(item, "Media", out var mediaArray)
                && mediaArray.ValueKind == JsonValueKind.Array)
            {
                var media = mediaArray.EnumerateArray().FirstOrDefault();
                if (media.ValueKind == JsonValueKind.Object)
                {
                    sourceId = MediaServerJson.String(media, "id");
                    method = ParseMethod(MediaServerJson.String(media, "videoDecision")
                                         ?? MediaServerJson.String(media, "audioDecision"));
                    if (MediaServerJson.TryGet(media, "Part", out var parts)
                        && parts.ValueKind == JsonValueKind.Array)
                    {
                        var part = parts.EnumerateArray().FirstOrDefault();
                        if (part.ValueKind == JsonValueKind.Object)
                        {
                            sourcePath = MediaServerJson.String(part, "file");
                            sourceId = MediaServerJson.String(part, "id") ?? sourceId;
                        }
                    }
                }
            }
            if (method == PlaybackDeliveryMethod.Unknown
                && MediaServerJson.TryGet(item, "TranscodeSession", out _))
                method = PlaybackDeliveryMethod.Transcode;

            result.Add(new PlaybackObservation
            {
                NativeSessionId = sessionId,
                UserName = MediaServerJson.String(user, "title"),
                ClientName = MediaServerJson.String(player, "product")
                             ?? MediaServerJson.String(player, "platform"),
                DeviceName = MediaServerJson.String(player, "title"),
                ItemId = MediaServerJson.String(item, "ratingKey"),
                Title = MediaServerJson.String(item, "title"),
                MediaType = MediaServerJson.String(item, "type"),
                SeriesName = MediaServerJson.String(item, "grandparentTitle"),
                SeasonNumber = MediaServerJson.Int32(item, "parentIndex"),
                EpisodeNumber = MediaServerJson.Int32(item, "index"),
                State = state,
                PositionMs = MediaServerJson.Int64(item, "viewOffset"),
                DurationMs = MediaServerJson.Int64(item, "duration"),
                DeliveryMethod = method,
                MediaSourceId = sourceId,
                MediaSourcePath = sourcePath,
            });
        }
        return result;
    }

    private static PlaybackState ParseState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "playing" => PlaybackState.Playing,
        "paused" => PlaybackState.Paused,
        "buffering" => PlaybackState.Buffering,
        _ => PlaybackState.Unknown,
    };

    private static PlaybackDeliveryMethod ParseMethod(string? value) =>
        value?.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal)
            .ToLowerInvariant() switch
        {
            "directplay" => PlaybackDeliveryMethod.DirectPlay,
            "copy" or "directstream" => PlaybackDeliveryMethod.DirectStream,
            "transcode" => PlaybackDeliveryMethod.Transcode,
            _ => PlaybackDeliveryMethod.Unknown,
        };
}
