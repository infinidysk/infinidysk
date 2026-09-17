from __future__ import annotations

from pathlib import Path
import textwrap

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def write(path: str, content: str) -> None:
    target = ROOT / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content, encoding="utf-8")


def replace_exact(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}: {old[:120]!r}")
    write(path, text.replace(old, new, 1))


# ---------------------------------------------------------------------------
# Configuration: reusable media-server instances + secret handling.
# ---------------------------------------------------------------------------
replace_exact(
    "backend/Config/ConfigKeys.cs",
    '    public const string MediaLibraryDir = "media.library-dir";\n',
    '    public const string MediaLibraryDir = "media.library-dir";\n'
    '    public const string MediaServersInstances = "media-servers.instances";\n',
)

write(
    "backend/Config/MediaServerConfig.cs",
    textwrap.dedent(r'''\
    using System.Text.Json.Serialization;

    namespace NzbWebDAV.Config;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MediaServerType
    {
        Plex,
        Emby,
        Jellyfin,
    }

    public sealed class MediaServerConfig
    {
        public List<MediaServerInstance> Instances { get; set; } = [];

        public IEnumerable<MediaServerInstance> GetEnabledInstances() =>
            (Instances ?? []).Where(instance => instance is not null && instance.Enabled);

        internal static void Validate(string key, MediaServerConfig? config)
        {
            if (config is null)
                throw new ArgumentException($"Config value for '{key}' must be a JSON object.");
            if (config.Instances is null)
                throw new ArgumentException($"Config value for '{key} Instances' must be an array.");

            var ids = new HashSet<Guid>();
            foreach (var instance in config.Instances)
            {
                if (instance is null)
                    throw new ArgumentException($"Config value for '{key} Instances' must not contain null entries.");
                if (instance.Id == Guid.Empty)
                    throw new ArgumentException($"Config value for '{key}' contains a media-server instance without an id.");
                if (!ids.Add(instance.Id))
                    throw new ArgumentException($"Config value for '{key}' contains duplicate media-server id '{instance.Id}'.");
                if (!Enum.IsDefined(instance.Type))
                    throw new ArgumentException($"Config value for '{key}' contains an unsupported media-server type.");
                if (string.IsNullOrWhiteSpace(instance.Name))
                    throw new ArgumentException($"Config value for '{key}' contains a media-server instance without a name.");
                if (!TryValidateBaseUrl(instance.BaseUrl))
                    throw new ArgumentException(
                        $"Config value for '{key}' media server '{instance.Name}' must use an absolute http(s) base URL without credentials, query, or fragment.");
                if (instance.Enabled && string.IsNullOrWhiteSpace(instance.Token))
                    throw new ArgumentException(
                        $"Config value for '{key}' media server '{instance.Name}' requires a token/API key while enabled.");
                if (instance.PathMappings is null)
                    throw new ArgumentException(
                        $"Config value for '{key}' media server '{instance.Name}' PathMappings must be an array.");

                foreach (var mapping in instance.PathMappings)
                {
                    if (mapping is null
                        || string.IsNullOrWhiteSpace(mapping.MediaServerPrefix)
                        || string.IsNullOrWhiteSpace(mapping.InfiniDyskPrefix))
                    {
                        throw new ArgumentException(
                            $"Config value for '{key}' media server '{instance.Name}' contains an incomplete path-prefix mapping.");
                    }
                    if (!Path.IsPathRooted(mapping.InfiniDyskPrefix))
                    {
                        throw new ArgumentException(
                            $"Config value for '{key}' media server '{instance.Name}' InfiniDysk path prefixes must be absolute paths.");
                    }
                }
            }
        }

        private static bool TryValidateBaseUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
            return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                   && string.IsNullOrEmpty(uri.UserInfo)
                   && string.IsNullOrEmpty(uri.Query)
                   && string.IsNullOrEmpty(uri.Fragment);
        }
    }

    public sealed class MediaServerInstance
    {
        public Guid Id { get; set; }
        public MediaServerType Type { get; set; }
        public string Name { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string Token { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public List<MediaServerPathMapping> PathMappings { get; set; } = [];

        internal bool RuntimeEquivalent(MediaServerInstance other)
        {
            if (Id != other.Id
                || Type != other.Type
                || Enabled != other.Enabled
                || !string.Equals(Name, other.Name, StringComparison.Ordinal)
                || !string.Equals(BaseUrl.TrimEnd('/'), other.BaseUrl.TrimEnd('/'), StringComparison.Ordinal)
                || !string.Equals(Token, other.Token, StringComparison.Ordinal))
                return false;

            if (PathMappings.Count != other.PathMappings.Count) return false;
            for (var i = 0; i < PathMappings.Count; i++)
            {
                var left = PathMappings[i];
                var right = other.PathMappings[i];
                if (!string.Equals(left.MediaServerPrefix, right.MediaServerPrefix, StringComparison.Ordinal)
                    || !string.Equals(left.InfiniDyskPrefix, right.InfiniDyskPrefix, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }

    public sealed class MediaServerPathMapping
    {
        public string MediaServerPrefix { get; set; } = "";
        public string InfiniDyskPrefix { get; set; } = "";
    }
    '''),
)

replace_exact(
    "backend/Config/ConfigManager.cs",
    '''    public ArrConfig GetArrConfig()\n    {\n        var defaultValue = new ArrConfig();\n        return GetConfigValue<ArrConfig>(ConfigKeys.ArrInstances) ?? defaultValue;\n    }\n''',
    '''    public ArrConfig GetArrConfig()\n    {\n        var defaultValue = new ArrConfig();\n        return GetConfigValue<ArrConfig>(ConfigKeys.ArrInstances) ?? defaultValue;\n    }\n\n    public MediaServerConfig GetMediaServerConfig()\n    {\n        return GetConfigValue<MediaServerConfig>(ConfigKeys.MediaServersInstances)\n               ?? new MediaServerConfig();\n    }\n''',
)

replace_exact(
    "backend/Config/ConfigManager.cs",
    '''                case ConfigKeys.ArrInstances:\n                    RequireJson<ArrConfig>(item.ConfigName, value, jsonOptions);\n                    break;\n''',
    '''                case ConfigKeys.ArrInstances:\n                    RequireJson<ArrConfig>(item.ConfigName, value, jsonOptions);\n                    break;\n\n                case ConfigKeys.MediaServersInstances:\n                    RequireJson<MediaServerConfig>(item.ConfigName, value, jsonOptions);\n                    RequireValidMediaServerConfig(item.ConfigName, value, jsonOptions);\n                    break;\n''',
)

replace_exact(
    "backend/Config/ConfigManager.cs",
    '''        void RequireHttpUrl(string key, string value)\n        {\n''',
    '''        void RequireValidMediaServerConfig(string key, string value, JsonSerializerOptions? options)\n        {\n            MediaServerConfig? config;\n            try\n            {\n                config = JsonSerializer.Deserialize<MediaServerConfig>(value, options);\n            }\n            catch (JsonException)\n            {\n                return; // RequireJson above produces the canonical parse error.\n            }\n            MediaServerConfig.Validate(key, config);\n        }\n\n        void RequireHttpUrl(string key, string value)\n        {\n''',
)

replace_exact(
    "backend/Config/ConfigSecretMasker.cs",
    '''        ["arr.instances"] = "ApiKey",\n        ["indexers.instances"] = "ApiKey",\n        ["usenet.providers"] = "Pass"\n''',
    '''        ["arr.instances"] = "ApiKey",\n        ["indexers.instances"] = "ApiKey",\n        ["media-servers.instances"] = "Token",\n        ["usenet.providers"] = "Pass"\n''',
)

replace_exact(
    "backend/Services/SupportPack/SupportPackRedactor.cs",
    '''        if (key is ConfigKeys.UsenetProviders or ConfigKeys.ArrInstances\n            or ConfigKeys.IndexersInstances or ConfigKeys.ProfilesInstances)\n''',
    '''        if (key is ConfigKeys.UsenetProviders or ConfigKeys.ArrInstances\n            or ConfigKeys.IndexersInstances or ConfigKeys.ProfilesInstances\n            or ConfigKeys.MediaServersInstances)\n''',
)

# ---------------------------------------------------------------------------
# Playback authority model + in-memory registry.
# ---------------------------------------------------------------------------
write(
    "backend/Models/Playback/PlaybackModels.cs",
    textwrap.dedent(r'''\
    using System.Text.Json.Serialization;

    namespace NzbWebDAV.Models.Playback;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PlaybackSourceType
    {
        Plex,
        Emby,
        Jellyfin,
        InfiniDysk,
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PlaybackState
    {
        Unknown,
        Playing,
        Paused,
        Buffering,
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PlaybackDeliveryMethod
    {
        Unknown,
        DirectPlay,
        DirectStream,
        Transcode,
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PlaybackFreshness
    {
        Fresh,
        Stale,
    }

    public readonly record struct PlaybackSessionKey(Guid SourceInstanceId, string NativeSessionId);

    public sealed record PlaybackObservation
    {
        public required string NativeSessionId { get; init; }
        public string? UserName { get; init; }
        public string? ClientName { get; init; }
        public string? DeviceName { get; init; }
        public string? ItemId { get; init; }
        public string? Title { get; init; }
        public string? MediaType { get; init; }
        public string? SeriesName { get; init; }
        public int? SeasonNumber { get; init; }
        public int? EpisodeNumber { get; init; }
        public PlaybackState State { get; init; }
        public long? PositionMs { get; init; }
        public long? DurationMs { get; init; }
        public PlaybackDeliveryMethod DeliveryMethod { get; init; }
        public string? MediaSourceId { get; init; }
        public string? MediaSourcePath { get; init; }
    }

    public sealed record MappedPlaybackObservation(PlaybackObservation Observation, Guid DavItemId);

    public sealed record AuthoritativePlaybackSession
    {
        public required PlaybackSessionKey Key { get; init; }
        public required string SourceInstanceName { get; init; }
        public required PlaybackSourceType SourceType { get; init; }
        public required string NativeSessionId { get; init; }
        public string? UserName { get; init; }
        public string? ClientName { get; init; }
        public string? DeviceName { get; init; }
        public string? ItemId { get; init; }
        public string? Title { get; init; }
        public string? MediaType { get; init; }
        public string? SeriesName { get; init; }
        public int? SeasonNumber { get; init; }
        public int? EpisodeNumber { get; init; }
        public PlaybackState State { get; init; }
        public long? PositionMs { get; init; }
        public long? DurationMs { get; init; }
        public PlaybackDeliveryMethod DeliveryMethod { get; init; }
        public string? MediaSourceId { get; init; }
        public string? MediaSourcePath { get; init; }
        public Guid DavItemId { get; init; }
        public DateTimeOffset LastConfirmedAt { get; init; }
        public PlaybackFreshness Freshness { get; init; }
    }

    public sealed record PlaybackAuthoritySnapshot
    {
        public Guid SourceInstanceId { get; init; }
        public required string SourceInstanceName { get; init; }
        public PlaybackSourceType SourceType { get; init; }
        public bool Available { get; init; }
        public bool IsStale { get; init; }
        public DateTimeOffset? LastSuccessfulPollAt { get; init; }
        public DateTimeOffset? LastFailureAt { get; init; }
        public string? LastErrorKind { get; init; }
    }
    '''),
)

write(
    "backend/Services/PlaybackSessionRegistry.cs",
    textwrap.dedent(r'''\
    using System.Collections.Concurrent;
    using NzbWebDAV.Config;
    using NzbWebDAV.Models.Playback;

    namespace NzbWebDAV.Services;

    /// <summary>
    /// In-memory authoritative playback state. Membership is driven by actual
    /// player/media-server state, never by WebDAV byte flow.
    /// </summary>
    public sealed class PlaybackSessionRegistry
    {
        internal static readonly Guid NativeExploreInstanceId =
            Guid.Parse("9f23ad65-cdaa-40fe-a5e4-c18fd77a1327");

        private readonly ConcurrentDictionary<PlaybackSessionKey, AuthoritativePlaybackSession> _sessions = new();
        private readonly ConcurrentDictionary<Guid, PlaybackAuthoritySnapshot> _authorities = new();

        public IReadOnlyList<AuthoritativePlaybackSession> Snapshot() =>
            _sessions.Values
                .OrderBy(session => session.SourceInstanceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(session => session.NativeSessionId, StringComparer.Ordinal)
                .ToList();

        public IReadOnlyList<PlaybackAuthoritySnapshot> AuthoritySnapshot() =>
            _authorities.Values
                .OrderBy(authority => authority.SourceInstanceName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public void ReconcileExternal(
            MediaServerInstance instance,
            IReadOnlyList<MappedPlaybackObservation> mappedSessions,
            DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(instance);
            ArgumentNullException.ThrowIfNull(mappedSessions);

            var sourceType = ToPlaybackSourceType(instance.Type);
            var desired = new Dictionary<PlaybackSessionKey, AuthoritativePlaybackSession>();
            foreach (var mapped in mappedSessions)
            {
                var observation = mapped.Observation;
                if (string.IsNullOrWhiteSpace(observation.NativeSessionId)) continue;
                var key = new PlaybackSessionKey(instance.Id, observation.NativeSessionId);
                desired[key] = FromObservation(
                    key, instance.Name, sourceType, observation, mapped.DavItemId, now, PlaybackFreshness.Fresh);
            }

            foreach (var pair in _sessions)
            {
                if (pair.Key.SourceInstanceId == instance.Id && !desired.ContainsKey(pair.Key))
                    _sessions.TryRemove(pair.Key, out _);
            }
            foreach (var pair in desired)
                _sessions[pair.Key] = pair.Value;

            _authorities[instance.Id] = new PlaybackAuthoritySnapshot
            {
                SourceInstanceId = instance.Id,
                SourceInstanceName = instance.Name,
                SourceType = sourceType,
                Available = true,
                IsStale = false,
                LastSuccessfulPollAt = now,
            };
        }

        public void MarkExternalFailure(
            MediaServerInstance instance,
            DateTimeOffset now,
            string errorKind)
        {
            var sourceType = ToPlaybackSourceType(instance.Type);
            foreach (var pair in _sessions)
            {
                if (pair.Key.SourceInstanceId != instance.Id) continue;
                _sessions[pair.Key] = pair.Value with { Freshness = PlaybackFreshness.Stale };
            }

            _authorities.TryGetValue(instance.Id, out var previous);
            _authorities[instance.Id] = new PlaybackAuthoritySnapshot
            {
                SourceInstanceId = instance.Id,
                SourceInstanceName = instance.Name,
                SourceType = sourceType,
                Available = false,
                IsStale = true,
                LastSuccessfulPollAt = previous?.LastSuccessfulPollAt,
                LastFailureAt = now,
                LastErrorKind = errorKind,
            };
        }

        public void ExpireStaleExternal(DateTimeOffset now, TimeSpan grace)
        {
            var cutoff = now - grace;
            foreach (var pair in _sessions)
            {
                if (pair.Value.SourceType == PlaybackSourceType.InfiniDysk
                    || pair.Value.Freshness != PlaybackFreshness.Stale
                    || pair.Value.LastConfirmedAt >= cutoff)
                    continue;
                _sessions.TryRemove(pair.Key, out _);
            }
        }

        public void RemoveInstance(Guid instanceId)
        {
            foreach (var pair in _sessions)
            {
                if (pair.Key.SourceInstanceId == instanceId)
                    _sessions.TryRemove(pair.Key, out _);
            }
            _authorities.TryRemove(instanceId, out _);
        }

        public void UpsertNative(
            string playerSession,
            Guid davItemId,
            string? title,
            string? mediaType,
            PlaybackState state,
            long? positionMs,
            long? durationMs,
            DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(playerSession))
                throw new ArgumentException("playerSession is required.", nameof(playerSession));
            if (davItemId == Guid.Empty)
                throw new ArgumentException("A native playback report requires an exact DavItemId.", nameof(davItemId));

            var key = new PlaybackSessionKey(NativeExploreInstanceId, playerSession);
            _sessions[key] = new AuthoritativePlaybackSession
            {
                Key = key,
                SourceInstanceName = "InfiniDysk",
                SourceType = PlaybackSourceType.InfiniDysk,
                NativeSessionId = playerSession,
                ClientName = "Explore",
                Title = title,
                MediaType = mediaType,
                State = state,
                PositionMs = positionMs,
                DurationMs = durationMs,
                DeliveryMethod = PlaybackDeliveryMethod.DirectPlay,
                DavItemId = davItemId,
                LastConfirmedAt = now,
                Freshness = PlaybackFreshness.Fresh,
            };
        }

        public void EndNative(string playerSession)
        {
            if (string.IsNullOrWhiteSpace(playerSession)) return;
            _sessions.TryRemove(new PlaybackSessionKey(NativeExploreInstanceId, playerSession), out _);
        }

        public void PruneNative(DateTimeOffset now, TimeSpan ttl)
        {
            var cutoff = now - ttl;
            foreach (var pair in _sessions)
            {
                if (pair.Value.SourceType == PlaybackSourceType.InfiniDysk
                    && pair.Value.LastConfirmedAt < cutoff)
                    _sessions.TryRemove(pair.Key, out _);
            }
        }

        private static AuthoritativePlaybackSession FromObservation(
            PlaybackSessionKey key,
            string sourceInstanceName,
            PlaybackSourceType sourceType,
            PlaybackObservation observation,
            Guid davItemId,
            DateTimeOffset now,
            PlaybackFreshness freshness) => new()
        {
            Key = key,
            SourceInstanceName = sourceInstanceName,
            SourceType = sourceType,
            NativeSessionId = observation.NativeSessionId,
            UserName = observation.UserName,
            ClientName = observation.ClientName,
            DeviceName = observation.DeviceName,
            ItemId = observation.ItemId,
            Title = observation.Title,
            MediaType = observation.MediaType,
            SeriesName = observation.SeriesName,
            SeasonNumber = observation.SeasonNumber,
            EpisodeNumber = observation.EpisodeNumber,
            State = observation.State,
            PositionMs = observation.PositionMs,
            DurationMs = observation.DurationMs,
            DeliveryMethod = observation.DeliveryMethod,
            MediaSourceId = observation.MediaSourceId,
            MediaSourcePath = observation.MediaSourcePath,
            DavItemId = davItemId,
            LastConfirmedAt = now,
            Freshness = freshness,
        };

        private static PlaybackSourceType ToPlaybackSourceType(MediaServerType type) => type switch
        {
            MediaServerType.Plex => PlaybackSourceType.Plex,
            MediaServerType.Emby => PlaybackSourceType.Emby,
            MediaServerType.Jellyfin => PlaybackSourceType.Jellyfin,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported media-server type."),
        };
    }
    '''),
)

# ---------------------------------------------------------------------------
# External media-server adapters. They normalize wire data only; exact
# InfiniDysk correlation is deliberately left to the later resolver layer.
# ---------------------------------------------------------------------------
write(
    "backend/Clients/MediaServers/IMediaPlaybackSessionSource.cs",
    textwrap.dedent(r'''\
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
    '''),
)

write(
    "backend/Clients/MediaServers/MediaServerJson.cs",
    textwrap.dedent(r'''\
    using System.Text.Json;

    namespace NzbWebDAV.Clients.MediaServers;

    internal static class MediaServerJson
    {
        public static bool TryGet(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
                return true;
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }
            value = default;
            return false;
        }

        public static string? String(JsonElement element, string name) =>
            TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        public static long? Int64(JsonElement element, string name)
        {
            if (!TryGet(element, name, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
            return null;
        }

        public static int? Int32(JsonElement element, string name)
        {
            var value = Int64(element, name);
            return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
        }

        public static bool? Bool(JsonElement element, string name)
        {
            if (!TryGet(element, name, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null,
            };
        }

        public static long? TicksToMilliseconds(long? ticks) => ticks is null ? null : ticks.Value / 10_000L;
    }
    '''),
)

write(
    "backend/Clients/MediaServers/MediaServerHttp.cs",
    textwrap.dedent(r'''\
    using System.Text.Json;

    namespace NzbWebDAV.Clients.MediaServers;

    internal static class MediaServerHttp
    {
        private const int MaxSessionResponseBytes = 4 * 1024 * 1024;

        public static Uri Endpoint(string baseUrl, string relativePath) =>
            new(baseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/'), UriKind.Absolute);

        public static async Task<JsonDocument> SendJsonAsync(
            HttpClient client,
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxSessionResponseBytes)
                throw new InvalidDataException("Media-server session response exceeded the size limit.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[32 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                if (buffer.Length + read > MaxSessionResponseBytes)
                    throw new InvalidDataException("Media-server session response exceeded the size limit.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
    '''),
)

write(
    "backend/Clients/MediaServers/PlexPlaybackSessionSource.cs",
    textwrap.dedent(r'''\
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
    '''),
)

write(
    "backend/Clients/MediaServers/EmbyJellyfinPlaybackSessionSource.cs",
    textwrap.dedent(r'''\
    using System.Net;
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
        private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        });
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
            if (root.ValueKind != JsonValueKind.Array) return [];
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
                var path = MediaServerJson.String(item, "Path");
                if (string.IsNullOrWhiteSpace(path)
                    && MediaServerJson.TryGet(item, "MediaSources", out var sources)
                    && sources.ValueKind == JsonValueKind.Array)
                {
                    foreach (var source in sources.EnumerateArray())
                    {
                        var candidateId = MediaServerJson.String(source, "Id");
                        if (mediaSourceId is not null
                            && !string.Equals(candidateId, mediaSourceId, StringComparison.Ordinal))
                            continue;
                        path = MediaServerJson.String(source, "Path");
                        mediaSourceId ??= candidateId;
                        if (!string.IsNullOrWhiteSpace(path)) break;
                    }
                }

                var paused = MediaServerJson.Bool(playState, "IsPaused") == true;
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
                    State = paused ? PlaybackState.Paused : PlaybackState.Playing,
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
    '''),
)

# ---------------------------------------------------------------------------
# Give transport sessions a first-class exact DavItemId without changing the
# existing websocket payload or ReadSession persistence semantics.
# ---------------------------------------------------------------------------
replace_exact(
    "backend/Services/ActiveReadRegistry.cs",
    '''        string? clientUserAgent = null,\n        string? clientIp = null,\n        string? playerSession = null)\n''',
    '''        string? clientUserAgent = null,\n        string? clientIp = null,\n        string? playerSession = null,\n        Guid? davItemId = null)\n''',
)

replace_exact(
    "backend/Services/ActiveReadRegistry.cs",
    '''                if (!string.IsNullOrEmpty(clientIp)) existing.ClientIp = clientIp;\n                return existingId;\n''',
    '''                if (!string.IsNullOrEmpty(clientIp)) existing.ClientIp = clientIp;\n                if (davItemId is { } resolvedId) existing.DavItemId = resolvedId;\n                return existingId;\n''',
)

replace_exact(
    "backend/Services/ActiveReadRegistry.cs",
    '''                PlayerSession = playerSession,\n                StartedAt = now,\n''',
    '''                PlayerSession = playerSession,\n                DavItemId = davItemId,\n                StartedAt = now,\n''',
)

replace_exact(
    "backend/Services/ActiveReadRegistry.cs",
    '''    public void UpdateInfo(Guid id, string? fileName, long? fileSize)\n    {\n        if (!_entries.TryGetValue(id, out var entry)) return;\n        if (!string.IsNullOrWhiteSpace(fileName)) entry.FileName = fileName;\n        if (fileSize is { } size) entry.FileSize = size;\n    }\n''',
    '''    public void UpdateInfo(Guid id, string? fileName, long? fileSize, Guid? davItemId = null)\n    {\n        if (!_entries.TryGetValue(id, out var entry)) return;\n        if (!string.IsNullOrWhiteSpace(fileName)) entry.FileName = fileName;\n        if (fileSize is { } size) entry.FileSize = size;\n        if (davItemId is { } resolvedId) entry.DavItemId = resolvedId;\n    }\n''',
)

replace_exact(
    "backend/Services/ActiveReadRegistry.cs",
    '''        public string? PlayerSession { get; init; }\n        public DateTimeOffset StartedAt { get; init; }\n''',
    '''        public string? PlayerSession { get; init; }\n        public Guid? DavItemId { get; set; }\n        public DateTimeOffset StartedAt { get; init; }\n''',
)

replace_exact(
    "backend/Services/ActiveReadRegistry.cs",
    '''        /// Most recent absolute file offset the player has been served (i.e. the\n        /// "where the read head is" position). Updated after every chunk so the\n        /// Right Now panel can show genuine playback position, not cumulative\n        /// transferred bytes (which over-counts on seek/rewind).\n''',
    '''        /// Most recent absolute file offset served by InfiniDysk. This is a\n        /// transport/source read head, not an authoritative viewer position: rclone\n        /// read-ahead may move it well ahead of an actual media-server player.\n''',
)

replace_exact(
    "backend/WebDav/DatabaseStoreIdFile.cs",
    '''    public SharedContentIdentity ContentIdentity =>\n        new(UniqueKey, davItem.FileBlobId, FileSize);\n''',
    '''    public SharedContentIdentity ContentIdentity =>\n        new(UniqueKey, davItem.FileBlobId, FileSize);\n    public Guid DavItemId => davItem.Id;\n''',
)

replace_exact(
    "backend/WebDav/Base/GetAndHeadHandlerPatch.cs",
    '''                    var sessionId = _activeReadRegistry.GetOrCreate(\n                        path, clientKey, fileName, stream.CanSeek ? stream.Length : null,\n                        userAgent, clientIp);\n''',
    '''                    var sessionId = _activeReadRegistry.GetOrCreate(\n                        path, clientKey, fileName, stream.CanSeek ? stream.Length : null,\n                        userAgent, clientIp, davItemId: (entry as DatabaseStoreIdFile)?.DavItemId);\n''',
)

replace_exact(
    "backend/Api/Controllers/GetWebdavItem/GetWebdavItemController.cs",
    '''        if (HttpContext.Items["readSessionId"] is Guid sid)\n            activeReadRegistry.UpdateInfo(sid, fileName, fileSize);\n''',
    '''        if (HttpContext.Items["readSessionId"] is Guid sid)\n            activeReadRegistry.UpdateInfo(sid, fileName, fileSize, idFile?.DavItemId);\n''',
)

replace_exact(
    "backend/Api/Controllers/GetWebdavItem/GetWebdavItemController.cs",
    '''        // 64 KB chunks; after each write report (bytesRead, absolutePosition)\n        // so the Right-Now panel can show real playback location and the\n        // throughput rate populates correctly.\n''',
    '''        // 64 KB chunks; after each write report (bytesRead, absolutePosition).\n        // absolutePosition is transport/source position, not authoritative viewer\n        // progress when an intermediary such as rclone is reading ahead.\n''',
)

# ---------------------------------------------------------------------------
# Focused tests for the slice.
# ---------------------------------------------------------------------------
write(
    "tests/NzbWebDAV.Tests/Config/MediaServerConfigTests.cs",
    textwrap.dedent(r'''\
    using System.Text.Json;
    using NzbWebDAV.Config;
    using NzbWebDAV.Database.Models;

    namespace NzbWebDAV.Tests.Config;

    public class MediaServerConfigTests
    {
        [Fact]
        public void ValidateConfigItems_AcceptsValidMultiInstanceConfig()
        {
            var config = new MediaServerConfig
            {
                Instances =
                [
                    new MediaServerInstance
                    {
                        Id = Guid.NewGuid(),
                        Type = MediaServerType.Plex,
                        Name = "Home Plex",
                        BaseUrl = "https://plex.example.test",
                        Token = "secret",
                        PathMappings =
                        [
                            new MediaServerPathMapping
                            {
                                MediaServerPrefix = "/movies",
                                InfiniDyskPrefix = "/library/movies",
                            },
                        ],
                    },
                ],
            };

            ConfigManager.ValidateConfigItems(
            [
                new ConfigItem
                {
                    ConfigName = ConfigKeys.MediaServersInstances,
                    ConfigValue = JsonSerializer.Serialize(config),
                },
            ], rejectUnknownJsonProperties: true);
        }

        [Fact]
        public void ValidateConfigItems_RejectsDuplicateInstanceIds()
        {
            var id = Guid.NewGuid();
            var json = $$"""
                {"Instances":[
                  {"Id":"{{id}}","Type":"Plex","Name":"A","BaseUrl":"http://a.test","Token":"a","Enabled":true,"PathMappings":[]},
                  {"Id":"{{id}}","Type":"Jellyfin","Name":"B","BaseUrl":"http://b.test","Token":"b","Enabled":true,"PathMappings":[]}
                ]}
                """;

            var error = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaServersInstances, ConfigValue = json },
            ], rejectUnknownJsonProperties: true));

            Assert.Contains("duplicate media-server id", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ConfigSecretMasker_MasksAndRestoresMediaServerTokens()
        {
            var masker = new ConfigSecretMasker("signing-key");
            var existing = """{"Instances":[{"Id":"11111111-1111-1111-1111-111111111111","Type":"Plex","Name":"Plex","BaseUrl":"http://plex.test","Token":"top-secret","Enabled":true,"PathMappings":[]}]}""";

            var masked = masker.MaskForResponse(ConfigKeys.MediaServersInstances, existing);
            Assert.DoesNotContain("top-secret", masked, StringComparison.Ordinal);
            Assert.Contains(ConfigSecretMasker.MaskPrefix, masked, StringComparison.Ordinal);

            var restored = masker.ResolveForUpdate(ConfigKeys.MediaServersInstances, masked, existing);
            using var document = JsonDocument.Parse(restored);
            var token = document.RootElement.GetProperty("Instances")[0].GetProperty("Token").GetString();
            Assert.Equal("top-secret", token);
        }
    }
    '''),
)

write(
    "tests/NzbWebDAV.Tests/Services/PlaybackSessionRegistryTests.cs",
    textwrap.dedent(r'''\
    using NzbWebDAV.Config;
    using NzbWebDAV.Models.Playback;
    using NzbWebDAV.Services;

    namespace NzbWebDAV.Tests.Services;

    public class PlaybackSessionRegistryTests
    {
        private static MediaServerInstance PlexInstance() => new()
        {
            Id = Guid.NewGuid(),
            Type = MediaServerType.Plex,
            Name = "Home Plex",
            BaseUrl = "http://plex.test",
            Token = "secret",
        };

        private static MappedPlaybackObservation Session(string id, Guid davItemId, PlaybackState state) =>
            new(new PlaybackObservation
            {
                NativeSessionId = id,
                Title = "Movie",
                State = state,
                PositionMs = 12_000,
                DurationMs = 60_000,
                MediaSourcePath = "/movies/movie.mkv",
            }, davItemId);

        [Fact]
        public void SuccessfulReconciliation_IsAuthoritativeAndRemovesAbsentSessions()
        {
            var registry = new PlaybackSessionRegistry();
            var instance = PlexInstance();
            var item = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            registry.ReconcileExternal(instance, [Session("s1", item, PlaybackState.Playing)], now);
            Assert.Single(registry.Snapshot());

            registry.ReconcileExternal(instance, [], now.AddSeconds(5));
            Assert.Empty(registry.Snapshot());
            Assert.True(registry.AuthoritySnapshot().Single().Available);
        }

        [Fact]
        public void TwoViewersOfSameDavItem_RemainDistinctSessions()
        {
            var registry = new PlaybackSessionRegistry();
            var instance = PlexInstance();
            var item = Guid.NewGuid();

            registry.ReconcileExternal(instance,
            [
                Session("alice", item, PlaybackState.Playing),
                Session("bob", item, PlaybackState.Paused),
            ], DateTimeOffset.UtcNow);

            var sessions = registry.Snapshot();
            Assert.Equal(2, sessions.Count);
            Assert.All(sessions, session => Assert.Equal(item, session.DavItemId));
            Assert.Contains(sessions, session => session.State == PlaybackState.Paused);
        }

        [Fact]
        public void PollFailure_MarksSessionsStaleAndRetainsThemUntilGraceExpires()
        {
            var registry = new PlaybackSessionRegistry();
            var instance = PlexInstance();
            var now = DateTimeOffset.UtcNow;
            registry.ReconcileExternal(instance, [Session("s1", Guid.NewGuid(), PlaybackState.Playing)], now);

            registry.MarkExternalFailure(instance, now.AddSeconds(5), "timeout");
            Assert.Equal(PlaybackFreshness.Stale, registry.Snapshot().Single().Freshness);
            Assert.True(registry.AuthoritySnapshot().Single().IsStale);

            registry.ExpireStaleExternal(now.AddSeconds(30), TimeSpan.FromSeconds(60));
            Assert.Single(registry.Snapshot());
            registry.ExpireStaleExternal(now.AddSeconds(61), TimeSpan.FromSeconds(60));
            Assert.Empty(registry.Snapshot());
        }

        [Fact]
        public void NativePausedSession_UsesIndependentHeartbeatTtl()
        {
            var registry = new PlaybackSessionRegistry();
            var now = DateTimeOffset.UtcNow;
            registry.UpsertNative("player-1", Guid.NewGuid(), "Movie", "Video",
                PlaybackState.Paused, 10_000, 20_000, now);

            registry.PruneNative(now.AddSeconds(30), TimeSpan.FromSeconds(90));
            Assert.Equal(PlaybackState.Paused, registry.Snapshot().Single().State);
            registry.PruneNative(now.AddSeconds(91), TimeSpan.FromSeconds(90));
            Assert.Empty(registry.Snapshot());
        }
    }
    '''),
)

write(
    "tests/NzbWebDAV.Tests/Clients/MediaServers/MediaPlaybackSessionSourceTests.cs",
    textwrap.dedent(r'''\
    using System.Text.Json;
    using NzbWebDAV.Clients.MediaServers;
    using NzbWebDAV.Models.Playback;

    namespace NzbWebDAV.Tests.Clients.MediaServers;

    public class MediaPlaybackSessionSourceTests
    {
        [Fact]
        public void PlexParser_NormalizesSessionStateProgressAndPath()
        {
            using var document = JsonDocument.Parse("""
            {
              "MediaContainer": {
                "Metadata": [{
                  "ratingKey": "123",
                  "type": "movie",
                  "title": "Dune: Part Two",
                  "duration": 9948000,
                  "viewOffset": 4462000,
                  "User": {"title": "alice"},
                  "Player": {"title": "Living Room Shield", "product": "Plex for Android", "state": "playing"},
                  "Session": {"id": "plex-session-1"},
                  "Media": [{"id": "media-1", "videoDecision": "directplay", "Part": [{"id": "part-1", "file": "/movies/Dune Part Two.mkv"}]}]
                }]
              }
            }
            """);

            var session = PlexPlaybackSessionSource.Parse(document.RootElement).Single();
            Assert.Equal("plex-session-1", session.NativeSessionId);
            Assert.Equal("alice", session.UserName);
            Assert.Equal("Living Room Shield", session.DeviceName);
            Assert.Equal(PlaybackState.Playing, session.State);
            Assert.Equal(4_462_000, session.PositionMs);
            Assert.Equal(PlaybackDeliveryMethod.DirectPlay, session.DeliveryMethod);
            Assert.Equal("/movies/Dune Part Two.mkv", session.MediaSourcePath);
        }

        [Fact]
        public void EmbyJellyfinParser_NormalizesPausedTicksAndMediaSource()
        {
            using var document = JsonDocument.Parse("""
            [{
              "Id": "session-2",
              "Client": "Jellyfin Android TV",
              "DeviceName": "Bedroom Shield",
              "UserName": "bob",
              "NowPlayingItem": {
                "Id": "episode-22",
                "Name": "Episode 3",
                "Type": "Episode",
                "SeriesName": "Example Show",
                "ParentIndexNumber": 2,
                "IndexNumber": 3,
                "RunTimeTicks": 36000000000,
                "MediaSources": [{"Id": "source-1", "Path": "/tv/Example Show/S02E03.mkv"}]
              },
              "PlayState": {
                "PositionTicks": 12000000000,
                "IsPaused": true,
                "MediaSourceId": "source-1",
                "PlayMethod": "DirectStream"
              }
            }]
            """);

            var session = EmbyJellyfinPlaybackSessionSource.Parse(document.RootElement).Single();
            Assert.Equal(PlaybackState.Paused, session.State);
            Assert.Equal(1_200_000, session.PositionMs);
            Assert.Equal(3_600_000, session.DurationMs);
            Assert.Equal(PlaybackDeliveryMethod.DirectStream, session.DeliveryMethod);
            Assert.Equal("source-1", session.MediaSourceId);
            Assert.Equal("/tv/Example Show/S02E03.mkv", session.MediaSourcePath);
        }
    }
    '''),
)

write(
    "tests/NzbWebDAV.Tests/Services/ActiveReadDavItemIdentityTests.cs",
    textwrap.dedent(r'''\
    using NzbWebDAV.Services;

    namespace NzbWebDAV.Tests.Services;

    public class ActiveReadDavItemIdentityTests
    {
        [Fact]
        public void ExactDavItemIdentity_IsStoredAndCanBeEnrichedLater()
        {
            var registry = new ActiveReadRegistry();
            var initialId = Guid.NewGuid();
            var session = registry.GetOrCreate(
                "/.ids/a/file", "client", "file.mkv", 100, davItemId: initialId);

            Assert.Equal(initialId, registry.Snapshot().Single().DavItemId);

            var resolvedId = Guid.NewGuid();
            registry.UpdateInfo(session, "friendly.mkv", 200, resolvedId);
            var entry = registry.Snapshot().Single();
            Assert.Equal(resolvedId, entry.DavItemId);
            Assert.Equal("friendly.mkv", entry.FileName);
            Assert.Equal(200, entry.FileSize);
        }
    }
    '''),
)

print("issue #1327 phase 3 slice A patch applied")
