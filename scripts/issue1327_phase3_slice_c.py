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

write(
    "backend/Services/PlaybackDavItemResolver.cs",
    textwrap.dedent(r'''
    using System.Collections.Concurrent;
    using NzbWebDAV.Config;
    using NzbWebDAV.Models.Playback;
    using NzbWebDAV.Utils;

    namespace NzbWebDAV.Services;

    /// <summary>
    /// Resolves only the currently-playing media source path to an exact InfiniDysk DavItem.
    /// It never performs a full library walk and never falls back to title/filename matching.
    /// </summary>
    public sealed class PlaybackDavItemResolver(ConfigManager configManager)
    {
        private readonly ConcurrentDictionary<CacheKey, Guid> _cache = new();

        public Guid? Resolve(MediaServerInstance instance, PlaybackObservation observation)
        {
            ArgumentNullException.ThrowIfNull(instance);
            ArgumentNullException.ThrowIfNull(observation);

            var sourcePath = observation.MediaSourcePath;
            if (string.IsNullOrWhiteSpace(sourcePath))
                return null;

            if (TryResolveDirectIdentity(sourcePath, configManager.GetRcloneMountDir(), out var directId))
                return directId;

            var translated = TranslateToInfiniDyskPath(instance, sourcePath);
            if (translated is null)
                return null;

            var libraryRoot = configManager.GetLibraryDir();
            if (string.IsNullOrWhiteSpace(libraryRoot))
                return null;

            var fullRoot = Path.GetFullPath(libraryRoot);
            var fullPath = Path.GetFullPath(translated);
            if (!IsUnderRoot(fullPath, fullRoot))
                return null;

            var key = new CacheKey(instance.Id, fullPath);
            if (_cache.TryGetValue(key, out var cachedId))
            {
                if (OrganizedLinksUtil.PathStillTargets(fullPath, cachedId, configManager))
                    return cachedId;
                _cache.TryRemove(key, out _);
            }

            var info = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(fullPath));
            var link = info switch
            {
                SymlinkAndStrmUtil.SymlinkInfo symlink =>
                    OrganizedLinksUtil.GetDavItemLink(symlink, configManager.GetRcloneMountDir()),
                SymlinkAndStrmUtil.StrmInfo strm =>
                    OrganizedLinksUtil.GetDavItemLink(strm),
                _ => null,
            };

            if (link is not { } exact)
                return null;

            _cache[key] = exact.DavItemId;
            return exact.DavItemId;
        }

        internal static string? TranslateToInfiniDyskPath(
            MediaServerInstance instance,
            string mediaSourcePath)
        {
            var normalizedSource = NormalizeRemotePath(mediaSourcePath);
            var mapping = instance.PathMappings
                .Where(candidate => PrefixMatches(
                    normalizedSource,
                    NormalizeRemotePath(candidate.MediaServerPrefix),
                    IsWindowsStyle(candidate.MediaServerPrefix)))
                .OrderByDescending(candidate => NormalizeRemotePath(candidate.MediaServerPrefix).Length)
                .FirstOrDefault();

            if (mapping is null)
            {
                return Path.IsPathRooted(mediaSourcePath)
                    ? mediaSourcePath
                    : null;
            }

            var normalizedPrefix = NormalizeRemotePath(mapping.MediaServerPrefix).TrimEnd('/');
            var suffix = normalizedSource[normalizedPrefix.Length..].TrimStart('/');
            var localSuffix = suffix.Replace('/', Path.DirectorySeparatorChar);
            return Path.Join(mapping.InfiniDyskPrefix, localSuffix);
        }

        internal static bool TryResolveDirectIdentity(
            string mediaSourcePath,
            string mountDir,
            out Guid davItemId)
        {
            davItemId = Guid.Empty;
            if (Uri.TryCreate(mediaSourcePath, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && TryParseIdsPath(uri.AbsolutePath, "/view/.ids/", out davItemId))
                return true;

            var normalized = NormalizeRemotePath(mediaSourcePath);
            if (TryParseIdsPath(normalized, "/.ids/", out davItemId))
                return true;

            var normalizedMount = NormalizeRemotePath(mountDir).TrimEnd('/');
            if (!PrefixMatches(normalized, normalizedMount, IsWindowsStyle(mountDir)))
                return false;

            var relative = normalized[normalizedMount.Length..];
            return TryParseIdsPath(relative, "/.ids/", out davItemId);
        }

        private static bool TryParseIdsPath(string path, string prefix, out Guid davItemId)
        {
            davItemId = Guid.Empty;
            var normalized = NormalizeRemotePath(path);
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            var remainder = normalized[prefix.Length..];
            if (string.IsNullOrWhiteSpace(remainder) || remainder.Contains('/'))
                return false;

            var dot = remainder.LastIndexOf('.');
            var idText = dot > 0 ? remainder[..dot] : remainder;
            return Guid.TryParse(idText, out davItemId);
        }

        private static bool IsUnderRoot(string path, string root)
        {
            var relative = Path.GetRelativePath(root, path);
            return relative != ".."
                   && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                   && !Path.IsPathRooted(relative);
        }

        private static bool PrefixMatches(string value, string prefix, bool ignoreCase)
        {
            prefix = prefix.TrimEnd('/');
            if (prefix.Length == 0 || value.Length < prefix.Length)
                return false;

            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!value.StartsWith(prefix, comparison))
                return false;

            return value.Length == prefix.Length || value[prefix.Length] == '/';
        }

        private static string NormalizeRemotePath(string path) =>
            path.Trim().Replace('\\', '/');

        private static bool IsWindowsStyle(string path) =>
            path.Contains('\\') || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');

        private readonly record struct CacheKey(Guid InstanceId, string LocalPath);
    }
    ''').lstrip(),
)

write(
    "backend/Services/MediaServerSessionPoller.cs",
    textwrap.dedent(r'''
    using System.Text.Json;
    using NzbWebDAV.Clients.MediaServers;
    using NzbWebDAV.Config;
    using NzbWebDAV.Models.Playback;
    using Serilog;

    namespace NzbWebDAV.Services;

    /// <summary>
    /// Reconciles the small current-session list from configured media servers.
    /// A successful response is authoritative. A failed request retains the last
    /// confirmed rows as stale for a bounded grace period.
    /// </summary>
    public sealed class MediaServerSessionPoller : BackgroundService
    {
        internal static readonly TimeSpan NormalPollInterval = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
        internal static readonly TimeSpan StaleGrace = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

        private readonly ConfigManager _configManager;
        private readonly PlaybackSessionRegistry _registry;
        private readonly PlaybackDavItemResolver _resolver;
        private readonly IReadOnlyDictionary<MediaServerType, IMediaPlaybackSessionSource> _sources;
        private readonly Func<double> _jitter;
        private readonly Dictionary<Guid, PollSchedule> _schedules = new();
        private readonly SemaphoreSlim _tickGate = new(1, 1);

        public MediaServerSessionPoller(
            ConfigManager configManager,
            PlaybackSessionRegistry registry,
            PlaybackDavItemResolver resolver,
            IEnumerable<IMediaPlaybackSessionSource> sources)
            : this(configManager, registry, resolver, sources, () => Random.Shared.NextDouble())
        {
        }

        internal MediaServerSessionPoller(
            ConfigManager configManager,
            PlaybackSessionRegistry registry,
            PlaybackDavItemResolver resolver,
            IEnumerable<IMediaPlaybackSessionSource> sources,
            Func<double> jitter)
        {
            _configManager = configManager;
            _registry = registry;
            _resolver = resolver;
            _sources = sources.ToDictionary(source => source.ServerType);
            _jitter = jitter;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollDueAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                    await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Log.Warning(exception, "Media-server session reconciliation tick failed");
                    await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                }
            }
        }

        internal async Task PollDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            await _tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var enabled = _configManager.GetMediaServerConfig().GetEnabledInstances().ToList();
                var enabledIds = enabled.Select(instance => instance.Id).ToHashSet();

                foreach (var id in _schedules.Keys.Where(id => !enabledIds.Contains(id)).ToList())
                {
                    _schedules.Remove(id);
                    _registry.RemoveInstance(id);
                }

                var due = new List<(MediaServerInstance Instance, PollSchedule Schedule)>();
                foreach (var instance in enabled)
                {
                    if (!_schedules.TryGetValue(instance.Id, out var schedule))
                    {
                        schedule = new PollSchedule(instance);
                        _schedules.Add(instance.Id, schedule);
                    }
                    else if (!schedule.Instance.RuntimeEquivalent(instance))
                    {
                        _registry.RemoveInstance(instance.Id);
                        schedule = new PollSchedule(instance);
                        _schedules[instance.Id] = schedule;
                    }
                    else
                    {
                        schedule.Instance = instance;
                    }

                    if (schedule.NextPollAt <= now)
                        due.Add((instance, schedule));
                }

                await Task.WhenAll(
                    due.Select(entry => PollInstanceAsync(
                        entry.Instance, entry.Schedule, now, cancellationToken))).ConfigureAwait(false);

                _registry.ExpireStaleExternal(now, StaleGrace);
            }
            finally
            {
                _tickGate.Release();
            }
        }

        private async Task PollInstanceAsync(
            MediaServerInstance instance,
            PollSchedule schedule,
            DateTimeOffset now,
            CancellationToken stoppingToken)
        {
            if (!_sources.TryGetValue(instance.Type, out var source))
            {
                MarkFailure(instance, schedule, now, "unsupported_source");
                return;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(RequestTimeout);

                var observations = await source.GetSessionsAsync(instance, timeout.Token).ConfigureAwait(false);
                var mapped = new List<MappedPlaybackObservation>();
                foreach (var observation in observations)
                {
                    var davItemId = _resolver.Resolve(instance, observation);
                    if (davItemId is { } exactId)
                        mapped.Add(new MappedPlaybackObservation(observation, exactId));
                }

                _registry.ReconcileExternal(instance, mapped, now);
                schedule.Failures = 0;
                schedule.NextPollAt = now + NormalPollInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                MarkFailure(instance, schedule, now, "timeout");
            }
            catch (HttpRequestException)
            {
                MarkFailure(instance, schedule, now, "http");
            }
            catch (JsonException)
            {
                MarkFailure(instance, schedule, now, "json");
            }
            catch (InvalidDataException)
            {
                MarkFailure(instance, schedule, now, "invalid_data");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.Debug(
                    exception,
                    "Media-server session poll failed for {ServerType} instance {InstanceName}",
                    instance.Type,
                    instance.Name);
                MarkFailure(instance, schedule, now, exception.GetType().Name);
            }
        }

        private void MarkFailure(
            MediaServerInstance instance,
            PollSchedule schedule,
            DateTimeOffset now,
            string errorKind)
        {
            schedule.Failures = Math.Min(schedule.Failures + 1, 32);
            schedule.NextPollAt = now + FailureDelay(schedule.Failures, _jitter());
            _registry.MarkExternalFailure(instance, now, errorKind);
        }

        internal static TimeSpan FailureDelay(int failureCount, double jitterSample)
        {
            var exponent = Math.Clamp(failureCount - 1, 0, 3);
            var seconds = Math.Min(30, 5 * (1 << exponent));
            var boundedJitter = Math.Clamp(jitterSample, 0d, 1d);
            return TimeSpan.FromSeconds(seconds * (1d + 0.2d * boundedJitter));
        }

        private sealed class PollSchedule(MediaServerInstance instance)
        {
            public MediaServerInstance Instance { get; set; } = instance;
            public DateTimeOffset NextPollAt { get; set; } = DateTimeOffset.MinValue;
            public int Failures { get; set; }
        }
    }
    ''').lstrip(),
)

write(
    "backend/Models/Playback/CurrentActivityModels.cs",
    textwrap.dedent(r'''
    namespace NzbWebDAV.Models.Playback;

    public enum TransportCorrelationScope
    {
        None,
        Session,
        File,
    }

    public sealed record CurrentActivitySnapshot
    {
        public required IReadOnlyList<CurrentPlaybackActivity> Playback { get; init; }
        public required IReadOnlyList<CurrentTransportActivity> Reads { get; init; }
        public required IReadOnlyList<PlaybackAuthoritySnapshot> Authorities { get; init; }
    }

    public sealed record CurrentPlaybackActivity
    {
        public required AuthoritativePlaybackSession Session { get; init; }
        public required IReadOnlyList<Guid> TransportReadIds { get; init; }
        public bool HasSharedFileTransport { get; init; }
    }

    public sealed record CurrentTransportActivity
    {
        public Guid Id { get; init; }
        public Guid? DavItemId { get; init; }
        public required string FileName { get; init; }
        public required string Path { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset LastActivityAt { get; init; }
        public long BytesRead { get; init; }
        public long BytesFetched { get; init; }
        public long SourceOffset { get; init; }
        public long? FileSize { get; init; }
        public string? ClientIp { get; init; }
        public string? ClientUserAgent { get; init; }
        public string? PlayerSession { get; init; }
        public TransportCorrelationScope CorrelationScope { get; init; }
        public int MatchingPlaybackSessionCount { get; init; }
        public bool Shared { get; init; }
        public required IReadOnlyList<CurrentProviderContribution> Providers { get; init; }
    }

    public sealed record CurrentProviderContribution
    {
        public required string Host { get; init; }
        public string? Nickname { get; init; }
        public long Segments { get; init; }
    }
    ''').lstrip(),
)

write(
    "backend/Services/CurrentActivityComposer.cs",
    textwrap.dedent(r'''
    using NzbWebDAV.Config;
    using NzbWebDAV.Models.Playback;
    using NzbWebDAV.Services.Metrics;

    namespace NzbWebDAV.Services;

    /// <summary>
    /// Joins authoritative playback state with transport telemetry without
    /// reclassifying transport as playback or assigning shared file traffic to viewers.
    /// </summary>
    public sealed class CurrentActivityComposer(
        PlaybackSessionRegistry playbackRegistry,
        ActiveReadRegistry activeReadRegistry,
        ProviderUsageTracker providerUsageTracker,
        ConfigManager configManager)
    {
        public CurrentActivitySnapshot Compose()
        {
            var playback = playbackRegistry.Snapshot();
            var reads = activeReadRegistry.Snapshot();
            var playbackByDav = playback
                .GroupBy(session => session.DavItemId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var readCountByDav = reads
                .Where(read => read.DavItemId.HasValue)
                .GroupBy(read => read.DavItemId!.Value)
                .ToDictionary(group => group.Key, group => group.Count());

            var usage = providerUsageTracker.SnapshotMany(reads.Select(read => read.Id));
            var displayByMetricsKey = ProviderUsageHelper.BuildDisplayByMetricsKey(
                configManager.GetUsenetProviderConfig().Providers);

            var transport = reads.Select(read =>
            {
                IReadOnlyList<AuthoritativePlaybackSession> matches = [];
                var scope = TransportCorrelationScope.None;

                var nativeMatch = playback.FirstOrDefault(session =>
                    session.SourceType == PlaybackSourceType.InfiniDysk
                    && read.PlayerSession is not null
                    && string.Equals(session.NativeSessionId, read.PlayerSession, StringComparison.Ordinal)
                    && (!read.DavItemId.HasValue || read.DavItemId.Value == session.DavItemId));

                if (nativeMatch is not null)
                {
                    matches = [nativeMatch];
                    scope = TransportCorrelationScope.Session;
                }
                else if (read.DavItemId is { } davItemId
                         && playbackByDav.TryGetValue(davItemId, out var fileMatches))
                {
                    matches = fileMatches;
                    scope = TransportCorrelationScope.File;
                }

                var shared = scope == TransportCorrelationScope.File
                             && (matches.Count > 1
                                 || (read.DavItemId is { } id
                                     && readCountByDav.GetValueOrDefault(id) > 1));

                return new CurrentTransportActivity
                {
                    Id = read.Id,
                    DavItemId = read.DavItemId,
                    FileName = read.FileName,
                    Path = read.Path,
                    StartedAt = read.StartedAt,
                    LastActivityAt = read.LastActivityAt,
                    BytesRead = Interlocked.Read(ref read.BytesRead),
                    BytesFetched = Interlocked.Read(ref read.BytesFetched),
                    SourceOffset = Interlocked.Read(ref read.CurrentOffset),
                    FileSize = read.FileSize,
                    ClientIp = read.ClientIp,
                    ClientUserAgent = read.ClientUserAgent,
                    PlayerSession = read.PlayerSession,
                    CorrelationScope = scope,
                    MatchingPlaybackSessionCount = matches.Count,
                    Shared = shared,
                    Providers = (usage.GetValueOrDefault(read.Id) ?? new Dictionary<string, long>())
                        .Select(pair =>
                        {
                            displayByMetricsKey.TryGetValue(pair.Key, out var display);
                            return new CurrentProviderContribution
                            {
                                Host = display.Host ?? pair.Key,
                                Nickname = display.Nickname,
                                Segments = pair.Value,
                            };
                        })
                        .OrderByDescending(provider => provider.Segments)
                        .ToList(),
                };
            }).ToList();

            var playbackRows = playback.Select(session =>
            {
                var matchingReads = transport
                    .Where(read =>
                        read.CorrelationScope == TransportCorrelationScope.Session
                            ? read.PlayerSession == session.NativeSessionId
                              && session.SourceType == PlaybackSourceType.InfiniDysk
                            : read.DavItemId == session.DavItemId
                              && read.CorrelationScope == TransportCorrelationScope.File)
                    .Select(read => read.Id)
                    .ToList();

                return new CurrentPlaybackActivity
                {
                    Session = session,
                    TransportReadIds = matchingReads,
                    HasSharedFileTransport = transport.Any(read =>
                        read.DavItemId == session.DavItemId
                        && read.CorrelationScope == TransportCorrelationScope.File
                        && read.Shared),
                };
            }).ToList();

            return new CurrentActivitySnapshot
            {
                Playback = playbackRows,
                Reads = transport,
                Authorities = playbackRegistry.AuthoritySnapshot(),
            };
        }
    }
    ''').lstrip(),
)

write(
    "backend/Services/CurrentActivityBroadcaster.cs",
    textwrap.dedent(r'''
    using System.Text.Json;
    using NzbWebDAV.Websocket;
    using Serilog;

    namespace NzbWebDAV.Services;

    public sealed class CurrentActivityBroadcaster(
        PlaybackSessionRegistry playbackRegistry,
        CurrentActivityComposer composer,
        IWebsocketPublisher websocketPublisher) : BackgroundService
    {
        internal static readonly TimeSpan NativeSessionTtl = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        private string? _lastPayload;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                    await BroadcastTickAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Log.Debug(exception, "CurrentActivityBroadcaster tick failed");
                }
            }
        }

        internal async Task BroadcastTickAsync(DateTimeOffset now)
        {
            playbackRegistry.PruneNative(now, NativeSessionTtl);
            playbackRegistry.ExpireStaleExternal(now, MediaServerSessionPoller.StaleGrace);

            if (!websocketPublisher.HasSubscribers(WebsocketTopic.CurrentActivity))
                return;

            var payload = JsonSerializer.Serialize(composer.Compose(), JsonOptions);
            if (payload == _lastPayload)
                return;

            _lastPayload = payload;
            await websocketPublisher.SendMessage(WebsocketTopic.CurrentActivity, payload).ConfigureAwait(false);
        }
    }
    ''').lstrip(),
)

replace_exact(
    "backend/Websocket/WebsocketTopic.cs",
    '    public static readonly WebsocketTopic ActiveReads = new("ar", TopicType.State);\n',
    '    public static readonly WebsocketTopic ActiveReads = new("ar", TopicType.State);\n'
    '    public static readonly WebsocketTopic CurrentActivity = new("ca", TopicType.State);\n',
)

replace_exact(
    "backend/Program.cs",
    '''                .AddHostedService<LogBroadcaster>()\n                .AddSingleton<ActiveReadRegistry>()\n''',
    '''                .AddHostedService<LogBroadcaster>()\n                .AddSingleton<ActiveReadRegistry>()\n                .AddSingleton<PlaybackSessionRegistry>()\n                .AddSingleton<PlaybackDavItemResolver>()\n                .AddSingleton<NzbWebDAV.Clients.MediaServers.IMediaPlaybackSessionSource, NzbWebDAV.Clients.MediaServers.PlexPlaybackSessionSource>()\n                .AddSingleton<NzbWebDAV.Clients.MediaServers.IMediaPlaybackSessionSource, NzbWebDAV.Clients.MediaServers.EmbyPlaybackSessionSource>()\n                .AddSingleton<NzbWebDAV.Clients.MediaServers.IMediaPlaybackSessionSource, NzbWebDAV.Clients.MediaServers.JellyfinPlaybackSessionSource>()\n                .AddSingleton<CurrentActivityComposer>()\n                .AddHostedService<MediaServerSessionPoller>()\n                .AddHostedService<CurrentActivityBroadcaster>()\n''',
)

write(
    "tests/NzbWebDAV.Tests/Services/PlaybackDavItemResolverTests.cs",
    textwrap.dedent(r'''
    using System.Text.Json;
    using NzbWebDAV.Config;
    using NzbWebDAV.Database.Models;
    using NzbWebDAV.Models.Playback;
    using NzbWebDAV.Services;

    namespace NzbWebDAV.Tests.Services;

    public sealed class PlaybackDavItemResolverTests : IDisposable
    {
        private readonly string _root = Path.Join(Path.GetTempPath(), $"infinidysk-playback-{Guid.NewGuid():N}");

        public PlaybackDavItemResolverTests() => Directory.CreateDirectory(_root);

        [Fact]
        public void Resolve_MapsOnlyTheActiveTranslatedStrmPath()
        {
            var library = Path.Join(_root, "library");
            Directory.CreateDirectory(library);
            var id = Guid.NewGuid();
            var strm = Path.Join(library, "movie.strm");
            File.WriteAllText(strm, $"http://localhost:3000/view/.ids/{id}.mkv");

            var config = Config(library);
            var resolver = new PlaybackDavItemResolver(config);
            var instance = Instance("/movies", library);

            var resolved = resolver.Resolve(instance, new PlaybackObservation
            {
                NativeSessionId = "s1",
                MediaSourcePath = "/movies/movie.strm",
            });

            Assert.Equal(id, resolved);
        }

        [Fact]
        public void Resolve_DoesNotUseFilenameOrTitleFallback()
        {
            var library = Path.Join(_root, "library");
            Directory.CreateDirectory(library);
            File.WriteAllText(Path.Join(library, "Dune.mkv"), "ordinary file");

            var resolver = new PlaybackDavItemResolver(Config(library));
            var resolved = resolver.Resolve(Instance("/movies", library), new PlaybackObservation
            {
                NativeSessionId = "s1",
                Title = "Dune",
                MediaSourcePath = "/movies/Dune.mkv",
            });

            Assert.Null(resolved);
        }

        [Fact]
        public void Resolve_RejectsTranslatedPathOutsideLibraryRoot()
        {
            var library = Path.Join(_root, "library");
            var outside = Path.Join(_root, "outside");
            Directory.CreateDirectory(library);
            Directory.CreateDirectory(outside);
            var id = Guid.NewGuid();
            File.WriteAllText(Path.Join(outside, "movie.strm"), $"http://localhost/view/.ids/{id}.mkv");

            var resolver = new PlaybackDavItemResolver(Config(library));
            var resolved = resolver.Resolve(Instance("/movies", outside), new PlaybackObservation
            {
                NativeSessionId = "s1",
                MediaSourcePath = "/movies/movie.strm",
            });

            Assert.Null(resolved);
        }

        [Fact]
        public void Resolve_DirectViewIdentityNeedsNoLibraryWalk()
        {
            var id = Guid.NewGuid();
            var resolver = new PlaybackDavItemResolver(new ConfigManager());

            var resolved = resolver.Resolve(Instance("/movies", "/unused"), new PlaybackObservation
            {
                NativeSessionId = "s1",
                MediaSourcePath = $"https://server.example/view/.ids/{id}.mkv",
            });

            Assert.Equal(id, resolved);
        }

        [Fact]
        public void Resolve_RevalidatesCachedLinkBeforeReuse()
        {
            var library = Path.Join(_root, "library");
            Directory.CreateDirectory(library);
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var strm = Path.Join(library, "movie.strm");
            File.WriteAllText(strm, $"http://localhost/view/.ids/{first}.mkv");

            var config = Config(library);
            var resolver = new PlaybackDavItemResolver(config);
            var instance = Instance("/movies", library);
            var observation = new PlaybackObservation { NativeSessionId = "s1", MediaSourcePath = "/movies/movie.strm" };

            Assert.Equal(first, resolver.Resolve(instance, observation));
            File.WriteAllText(strm, $"http://localhost/view/.ids/{second}.mkv");
            Assert.Equal(second, resolver.Resolve(instance, observation));
        }

        private static ConfigManager Config(string library)
        {
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = library },
                new ConfigItem { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = "/mnt/infinidysk" },
            ]);
            return config;
        }

        private static MediaServerInstance Instance(string remotePrefix, string localPrefix) => new()
        {
            Id = Guid.NewGuid(),
            Type = MediaServerType.Plex,
            Name = "Plex",
            BaseUrl = "http://plex.test",
            Token = "secret",
            PathMappings =
            [
                new MediaServerPathMapping
                {
                    MediaServerPrefix = remotePrefix,
                    InfiniDyskPrefix = localPrefix,
                },
            ],
        };

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
    ''').lstrip(),
)

write(
    "tests/NzbWebDAV.Tests/Services/MediaServerSessionPollerTests.cs",
    textwrap.dedent(r'''
    using System.Text.Json;
    using NzbWebDAV.Clients.MediaServers;
    using NzbWebDAV.Config;
    using NzbWebDAV.Database.Models;
    using NzbWebDAV.Models.Playback;
    using NzbWebDAV.Services;

    namespace NzbWebDAV.Tests.Services;

    public class MediaServerSessionPollerTests
    {
        [Fact]
        public async Task SuccessfulPoll_IsAuthoritativeAndRestartSafe()
        {
            var id = Guid.NewGuid();
            var instance = Instance();
            var source = new FakeSource(MediaServerType.Plex);
            source.SetResult([Observation("s1", id)]);
            var registry = new PlaybackSessionRegistry();
            var poller = Poller(instance, source, registry);
            var now = DateTimeOffset.UtcNow;

            await poller.PollDueAsync(now, CancellationToken.None);
            Assert.Single(registry.Snapshot());
            Assert.Equal(id, registry.Snapshot().Single().DavItemId);

            source.SetResult([]);
            await poller.PollDueAsync(now + TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Empty(registry.Snapshot());
        }

        [Fact]
        public async Task FailedWholeServerPoll_RetainsStaleRowsThenExpiresAfterGrace()
        {
            var id = Guid.NewGuid();
            var instance = Instance();
            var source = new FakeSource(MediaServerType.Plex);
            source.SetResult([Observation("s1", id)]);
            var registry = new PlaybackSessionRegistry();
            var poller = Poller(instance, source, registry);
            var now = DateTimeOffset.UtcNow;

            await poller.PollDueAsync(now, CancellationToken.None);
            source.SetException(new HttpRequestException("server unavailable"));
            await poller.PollDueAsync(now + TimeSpan.FromSeconds(5), CancellationToken.None);

            Assert.Single(registry.Snapshot());
            Assert.Equal(PlaybackFreshness.Stale, registry.Snapshot().Single().Freshness);
            Assert.True(registry.AuthoritySnapshot().Single().IsStale);

            await poller.PollDueAsync(now + TimeSpan.FromSeconds(61), CancellationToken.None);
            Assert.Empty(registry.Snapshot());
        }

        [Fact]
        public async Task MultipleEnabledInstances_PollInParallel()
        {
            var first = Instance();
            var second = Instance();
            second.Id = Guid.NewGuid();
            second.Name = "Plex 2";
            var source = new ConcurrentProbeSource();
            var registry = new PlaybackSessionRegistry();
            var config = Config(first, second);
            var poller = new MediaServerSessionPoller(
                config,
                registry,
                new PlaybackDavItemResolver(config),
                [source],
                () => 0);

            await poller.PollDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.True(source.MaxConcurrent >= 2);
        }

        [Theory]
        [InlineData(1, 5)]
        [InlineData(2, 10)]
        [InlineData(3, 20)]
        [InlineData(4, 30)]
        [InlineData(8, 30)]
        public void FailureDelay_UsesBoundedExponentialSchedule(int failures, int seconds)
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), MediaServerSessionPoller.FailureDelay(failures, 0));
            Assert.True(MediaServerSessionPoller.FailureDelay(failures, 1) > TimeSpan.FromSeconds(seconds));
        }

        private static MediaServerSessionPoller Poller(
            MediaServerInstance instance,
            IMediaPlaybackSessionSource source,
            PlaybackSessionRegistry registry)
        {
            var config = Config(instance);
            return new MediaServerSessionPoller(
                config,
                registry,
                new PlaybackDavItemResolver(config),
                [source],
                () => 0);
        }

        private static ConfigManager Config(params MediaServerInstance[] instances)
        {
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = ConfigKeys.MediaServersInstances,
                    ConfigValue = JsonSerializer.Serialize(new MediaServerConfig { Instances = instances.ToList() }),
                },
            ]);
            return config;
        }

        private static MediaServerInstance Instance() => new()
        {
            Id = Guid.NewGuid(),
            Type = MediaServerType.Plex,
            Name = "Plex",
            BaseUrl = "http://plex.test",
            Token = "secret",
        };

        private static PlaybackObservation Observation(string session, Guid id) => new()
        {
            NativeSessionId = session,
            State = PlaybackState.Playing,
            PositionMs = 1234,
            MediaSourcePath = $"https://plex.test/view/.ids/{id}.mkv",
        };

        private sealed class FakeSource(MediaServerType serverType) : IMediaPlaybackSessionSource
        {
            private IReadOnlyList<PlaybackObservation> _result = [];
            private Exception? _exception;
            public MediaServerType ServerType => serverType;

            public void SetResult(IReadOnlyList<PlaybackObservation> result)
            {
                _result = result;
                _exception = null;
            }

            public void SetException(Exception exception) => _exception = exception;

            public Task<IReadOnlyList<PlaybackObservation>> GetSessionsAsync(
                MediaServerInstance instance,
                CancellationToken cancellationToken)
            {
                if (_exception is not null)
                    throw _exception;
                return Task.FromResult(_result);
            }
        }

        private sealed class ConcurrentProbeSource : IMediaPlaybackSessionSource
        {
            private int _active;
            private int _maxConcurrent;
            public MediaServerType ServerType => MediaServerType.Plex;
            public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

            public async Task<IReadOnlyList<PlaybackObservation>> GetSessionsAsync(
                MediaServerInstance instance,
                CancellationToken cancellationToken)
            {
                var active = Interlocked.Increment(ref _active);
                while (true)
                {
                    var current = Volatile.Read(ref _maxConcurrent);
                    if (active <= current || Interlocked.CompareExchange(ref _maxConcurrent, active, current) == current)
                        break;
                }

                try
                {
                    await Task.Delay(50, cancellationToken);
                    return [];
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
        }
    }
    ''').lstrip(),
)

write(
    "tests/NzbWebDAV.Tests/Services/CurrentActivityComposerTests.cs",
    textwrap.dedent(r'''
    using NzbWebDAV.Config;
    using NzbWebDAV.Models.Playback;
    using NzbWebDAV.Services;

    namespace NzbWebDAV.Tests.Services;

    public class CurrentActivityComposerTests
    {
        [Fact]
        public void TwoViewersOnOneDavItem_KeepFileTransportShared()
        {
            var playback = new PlaybackSessionRegistry();
            var active = new ActiveReadRegistry();
            var usage = new ProviderUsageTracker(active);
            var instance = Instance();
            var item = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            playback.ReconcileExternal(instance,
            [
                Mapped("alice", item, 10_000),
                Mapped("bob", item, 20_000),
            ], now);

            var readId = active.GetOrCreate("/.ids/file", "rclone", "movie.mkv", 100, davItemId: item);
            active.Touch(readId, 50, currentOffset: 90);
            using (usage.BeginScope(readId))
                usage.RecordSuccess("news.example.test");

            var snapshot = new CurrentActivityComposer(playback, active, usage, new ConfigManager()).Compose();

            Assert.Equal(2, snapshot.Playback.Count);
            var read = Assert.Single(snapshot.Reads);
            Assert.Equal(TransportCorrelationScope.File, read.CorrelationScope);
            Assert.Equal(2, read.MatchingPlaybackSessionCount);
            Assert.True(read.Shared);
            Assert.Equal(90, read.SourceOffset);
            Assert.All(snapshot.Playback, row => Assert.True(row.HasSharedFileTransport));
            Assert.Equal([10_000L, 20_000L], snapshot.Playback.Select(row => row.Session.PositionMs!.Value).Order());
        }

        [Fact]
        public void UnmatchedTransport_RemainsReadOnlyActivity()
        {
            var active = new ActiveReadRegistry();
            active.GetOrCreate("/scan", "rclone", "scan.mkv", 100);

            var snapshot = new CurrentActivityComposer(
                new PlaybackSessionRegistry(),
                active,
                new ProviderUsageTracker(active),
                new ConfigManager()).Compose();

            Assert.Empty(snapshot.Playback);
            Assert.Equal(TransportCorrelationScope.None, Assert.Single(snapshot.Reads).CorrelationScope);
        }

        [Fact]
        public void NativePlayerSession_HasSessionLevelCorrelation()
        {
            var playback = new PlaybackSessionRegistry();
            var active = new ActiveReadRegistry();
            var item = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            playback.UpsertNative("player-1", item, "Movie", "Video", PlaybackState.Paused, 12_000, 60_000, now);
            active.GetOrCreate(
                "/.ids/movie",
                "browser",
                "movie.mkv",
                100,
                playerSession: "player-1",
                davItemId: item);

            var snapshot = new CurrentActivityComposer(
                playback,
                active,
                new ProviderUsageTracker(active),
                new ConfigManager()).Compose();

            Assert.Equal(PlaybackState.Paused, Assert.Single(snapshot.Playback).Session.State);
            Assert.Equal(12_000, snapshot.Playback.Single().Session.PositionMs);
            Assert.Equal(TransportCorrelationScope.Session, Assert.Single(snapshot.Reads).CorrelationScope);
        }

        private static MediaServerInstance Instance() => new()
        {
            Id = Guid.NewGuid(),
            Type = MediaServerType.Plex,
            Name = "Plex",
            BaseUrl = "http://plex.test",
            Token = "secret",
        };

        private static MappedPlaybackObservation Mapped(string session, Guid id, long position) =>
            new(new PlaybackObservation
            {
                NativeSessionId = session,
                State = PlaybackState.Playing,
                PositionMs = position,
                DurationMs = 60_000,
            }, id);
    }
    ''').lstrip(),
)

write(
    "tests/NzbWebDAV.Tests/Websocket/CurrentActivityTopicTests.cs",
    textwrap.dedent(r'''
    using NzbWebDAV.Websocket;

    namespace NzbWebDAV.Tests.Websocket;

    public class CurrentActivityTopicTests
    {
        [Fact]
        public void CurrentActivity_IsIndependentReplayableStateTopic()
        {
            Assert.True(WebsocketTopic.TryGetByName("ca", out var topic));
            Assert.Same(WebsocketTopic.CurrentActivity, topic);
            Assert.Equal(WebsocketTopic.TopicType.State, topic!.Type);
            Assert.NotSame(WebsocketTopic.ActiveReads, topic);
        }
    }
    ''').lstrip(),
)

print("issue #1327 phase 3 slice C patch applied")

# trigger fork validation after generic runner update
