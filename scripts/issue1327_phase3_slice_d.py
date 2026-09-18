from pathlib import Path
from textwrap import dedent

ROOT = Path(".")

def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")

def write(path: str, content: str) -> None:
    target = ROOT / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content, encoding="utf-8")

def replace_once(path: str, old: str, new: str) -> None:
    content = read(path)
    count = content.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}")
    write(path, content.replace(old, new, 1))

# ActiveReadRegistry: exact, fail-closed native player identity lookup.
replace_once(
    "backend/Services/ActiveReadRegistry.cs",
    """    public IReadOnlyList<Entry> Snapshot()
    {
""",
    """    public bool TryResolveDavItemIdForPlayerSession(string playerSession, out Guid davItemId)
    {
        davItemId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(playerSession))
            return false;

        var cutoff = DateTimeOffset.UtcNow - ActivityWindow;
        var matches = _entries.Values
            .Where(entry =>
                entry.LastActivityAt >= cutoff
                && entry.DavItemId.HasValue
                && string.Equals(entry.PlayerSession, playerSession, StringComparison.Ordinal))
            .Select(entry => entry.DavItemId!.Value)
            .Distinct()
            .Take(2)
            .ToList();

        if (matches.Count != 1)
            return false;

        davItemId = matches[0];
        return true;
    }

    public IReadOnlyList<Entry> Snapshot()
    {
""",
)

# PlaybackSessionRegistry: once identity is established, subsequent heartbeats can
# refresh authority without requiring active transport.
replace_once(
    "backend/Services/PlaybackSessionRegistry.cs",
    """    public void UpsertNative(
""",
    """    public bool TryGetNativeDavItemId(string playerSession, out Guid davItemId)
    {
        davItemId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(playerSession))
            return false;

        if (!_sessions.TryGetValue(
                new PlaybackSessionKey(NativeExploreInstanceId, playerSession),
                out var session))
            return false;

        davItemId = session.DavItemId;
        return davItemId != Guid.Empty;
    }

    public void UpsertNative(
""",
)

# Native reporting service: the browser never supplies DavItemId.
write(
    "backend/Services/NativePlaybackSessionService.cs",
    dedent("""        using NzbWebDAV.Models.Playback;

        namespace NzbWebDAV.Services;

        /// <summary>
        /// Accepts authoritative lifecycle reports from InfiniDysk's Explore player.
        /// The browser supplies only a short correlation token and player state; exact
        /// DavItem identity must already be proven by a matching backend Active Read.
        /// </summary>
        public sealed class NativePlaybackSessionService(
            ActiveReadRegistry activeReadRegistry,
            PlaybackSessionRegistry playbackRegistry)
        {
            public bool Report(
                string playerSession,
                PlaybackState state,
                long? positionMs,
                long? durationMs,
                string? title,
                string? mediaType,
                DateTimeOffset now)
            {
                if (!playbackRegistry.TryGetNativeDavItemId(playerSession, out var davItemId)
                    && !activeReadRegistry.TryResolveDavItemIdForPlayerSession(playerSession, out davItemId))
                    return false;

                playbackRegistry.UpsertNative(
                    playerSession,
                    davItemId,
                    title,
                    mediaType,
                    state,
                    positionMs,
                    durationMs,
                    now);
                return true;
            }

            public void End(string playerSession) => playbackRegistry.EndNative(playerSession);
        }
        """),
)

# Authenticated, bounded backend API capability. Frontend wiring intentionally waits
# for Phase 4.
write(
    "backend/Api/Controllers/Playback/NativePlaybackController.cs",
    dedent("""        using System.Text.Json;
        using System.Text.Json.Serialization;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;
        using NzbWebDAV.Api.Controllers.GetWebdavItem;
        using NzbWebDAV.Models.Playback;
        using NzbWebDAV.Services;

        namespace NzbWebDAV.Api.Controllers.Playback;

        [ApiController]
        [Route("api/playback/native")]
        [RequestSizeLimit(16 * 1024)]
        public sealed class NativePlaybackController(
            NativePlaybackSessionService nativePlaybackSessionService) : PostOnlyApiController
        {
            private const int MaxTitleLength = 512;
            private const int MaxMediaTypeLength = 64;

            protected override async Task<IActionResult> HandleRequest()
            {
                NativePlaybackRequest? request;
                try
                {
                    request = await Request.ReadFromJsonAsync<NativePlaybackRequest>(
                            cancellationToken: HttpContext.RequestAborted)
                        .ConfigureAwait(false);
                }
                catch (JsonException exception)
                {
                    throw new BadHttpRequestException("Invalid native playback report.", exception);
                }

                if (request is null)
                    throw new BadHttpRequestException("Native playback report is required.");

                var playerSession = GetWebdavItemRequest.NormalizePlayerSession(request.PlayerSession);
                if (playerSession is null)
                    throw new BadHttpRequestException("A valid playerSession is required.");

                if (!Enum.IsDefined(request.Event))
                    throw new BadHttpRequestException("Unsupported native playback event.");

                if (request.Event == NativePlaybackEvent.End)
                {
                    nativePlaybackSessionService.End(playerSession);
                    return Ok(new { status = true });
                }

                ValidateReport(request);

                if (!nativePlaybackSessionService.Report(
                        playerSession,
                        request.State,
                        request.PositionMs,
                        request.DurationMs,
                        request.Title,
                        request.MediaType,
                        DateTimeOffset.UtcNow))
                {
                    return Conflict(new
                    {
                        status = false,
                        error = "playerSession has no exact active InfiniDysk item association.",
                    });
                }

                return Ok(new { status = true });
            }

            private static void ValidateReport(NativePlaybackRequest request)
            {
                if (!Enum.IsDefined(request.State) || request.State == PlaybackState.Unknown)
                    throw new BadHttpRequestException("A concrete playback state is required.");
                if (request.PositionMs is < 0)
                    throw new BadHttpRequestException("positionMs cannot be negative.");
                if (request.DurationMs is < 0)
                    throw new BadHttpRequestException("durationMs cannot be negative.");
                if (request.Title?.Length > MaxTitleLength)
                    throw new BadHttpRequestException($"title cannot exceed {MaxTitleLength} characters.");
                if (request.MediaType?.Length > MaxMediaTypeLength)
                    throw new BadHttpRequestException($"mediaType cannot exceed {MaxMediaTypeLength} characters.");
            }
        }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum NativePlaybackEvent
        {
            Report,
            End,
        }

        public sealed class NativePlaybackRequest
        {
            public string? PlayerSession { get; init; }
            public NativePlaybackEvent Event { get; init; } = NativePlaybackEvent.Report;
            public PlaybackState State { get; init; }
            public long? PositionMs { get; init; }
            public long? DurationMs { get; init; }
            public string? Title { get; init; }
            public string? MediaType { get; init; }
        }
        """),
)

replace_once(
    "backend/Program.cs",
    """                .AddSingleton<PlaybackSessionRegistry>()
                .AddSingleton<PlaybackDavItemResolver>()
""",
    """                .AddSingleton<PlaybackSessionRegistry>()
                .AddSingleton<NativePlaybackSessionService>()
                .AddSingleton<PlaybackDavItemResolver>()
""",
)

# Media-server session credentials must never be auto-forwarded across redirects.
replace_once(
    "backend/Clients/MediaServers/MediaServerHttp.cs",
    "using System.Text.Json;\n",
    "using System.Net;\nusing System.Text.Json;\n",
)
replace_once(
    "backend/Clients/MediaServers/MediaServerHttp.cs",
    """    private const int MaxSessionResponseBytes = 4 * 1024 * 1024;

""",
    """    private const int MaxSessionResponseBytes = 4 * 1024 * 1024;

    internal static SocketsHttpHandler CreateSessionHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
    };

""",
)

for path in [
    "backend/Clients/MediaServers/PlexPlaybackSessionSource.cs",
    "backend/Clients/MediaServers/EmbyJellyfinPlaybackSessionSource.cs",
]:
    replace_once(path, "using System.Net;\n", "")
    replace_once(
        path,
        """    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    });
""",
        """    private static readonly HttpClient SharedClient = new(MediaServerHttp.CreateSessionHandler());
""",
    )

# Focused native lifecycle tests.
write(
    "tests/NzbWebDAV.Tests/Services/NativePlaybackSessionServiceTests.cs",
    dedent("""        using NzbWebDAV.Models.Playback;
        using NzbWebDAV.Services;

        namespace NzbWebDAV.Tests.Services;

        public class NativePlaybackSessionServiceTests
        {
            [Fact]
            public void Report_RejectsUnknownPlayerSessionWithoutExactBackendIdentity()
            {
                var registry = new PlaybackSessionRegistry();
                var service = new NativePlaybackSessionService(new ActiveReadRegistry(), registry);

                var accepted = service.Report(
                    "player-1",
                    PlaybackState.Playing,
                    1_000,
                    10_000,
                    "Movie",
                    "video",
                    DateTimeOffset.UtcNow);

                Assert.False(accepted);
                Assert.Empty(registry.Snapshot());
            }

            [Fact]
            public void Report_EstablishesFromActiveReadThenSurvivesTransportExpiry()
            {
                var activeReads = new ActiveReadRegistry();
                var registry = new PlaybackSessionRegistry();
                var service = new NativePlaybackSessionService(activeReads, registry);
                var davItemId = Guid.NewGuid();
                activeReads.GetOrCreate(
                    "/.ids/movie",
                    "browser",
                    "movie.mkv",
                    100,
                    playerSession: "player-1",
                    davItemId: davItemId);
                var now = DateTimeOffset.UtcNow;

                Assert.True(service.Report(
                    "player-1",
                    PlaybackState.Playing,
                    1_000,
                    10_000,
                    "Movie",
                    "video",
                    now));

                activeReads.PruneExpired(now.AddSeconds(16));
                Assert.Empty(activeReads.Snapshot());

                Assert.True(service.Report(
                    "player-1",
                    PlaybackState.Paused,
                    2_000,
                    10_000,
                    "Movie",
                    "video",
                    now.AddSeconds(30)));

                var session = Assert.Single(registry.Snapshot());
                Assert.Equal(davItemId, session.DavItemId);
                Assert.Equal(PlaybackState.Paused, session.State);
                Assert.Equal(2_000, session.PositionMs);
                Assert.Equal(now.AddSeconds(30), session.LastConfirmedAt);
            }

            [Fact]
            public void Report_FailsClosedWhenOnePlayerSessionMapsToDifferentActiveItems()
            {
                var activeReads = new ActiveReadRegistry();
                var registry = new PlaybackSessionRegistry();
                var service = new NativePlaybackSessionService(activeReads, registry);
                activeReads.GetOrCreate(
                    "/.ids/one",
                    "browser",
                    "one.mkv",
                    100,
                    playerSession: "player-1",
                    davItemId: Guid.NewGuid());
                activeReads.GetOrCreate(
                    "/.ids/two",
                    "browser",
                    "two.mkv",
                    100,
                    playerSession: "player-1",
                    davItemId: Guid.NewGuid());

                Assert.False(service.Report(
                    "player-1",
                    PlaybackState.Playing,
                    null,
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow));
                Assert.Empty(registry.Snapshot());
            }

            [Fact]
            public void End_RemovesEstablishedNativeSession()
            {
                var activeReads = new ActiveReadRegistry();
                var registry = new PlaybackSessionRegistry();
                var service = new NativePlaybackSessionService(activeReads, registry);
                activeReads.GetOrCreate(
                    "/.ids/movie",
                    "browser",
                    "movie.mkv",
                    100,
                    playerSession: "player-1",
                    davItemId: Guid.NewGuid());

                Assert.True(service.Report(
                    "player-1",
                    PlaybackState.Buffering,
                    500,
                    10_000,
                    null,
                    "video",
                    DateTimeOffset.UtcNow));
                Assert.Single(registry.Snapshot());

                service.End("player-1");

                Assert.Empty(registry.Snapshot());
            }
        }
        """),
)

# Active-read exact identity helper regression.
replace_once(
    "tests/NzbWebDAV.Tests/Services/ActiveReadDavItemIdentityTests.cs",
    """    }
}
""",
    """    }

    [Fact]
    public void PlayerSessionIdentity_FailsClosedWhenAssociationIsAmbiguous()
    {
        var registry = new ActiveReadRegistry();
        var first = Guid.NewGuid();
        registry.GetOrCreate(
            "/.ids/one", "client", "one.mkv", 100,
            playerSession: "player-1", davItemId: first);

        Assert.True(registry.TryResolveDavItemIdForPlayerSession("player-1", out var resolved));
        Assert.Equal(first, resolved);

        registry.GetOrCreate(
            "/.ids/two", "client", "two.mkv", 100,
            playerSession: "player-1", davItemId: Guid.NewGuid());

        Assert.False(registry.TryResolveDavItemIdForPlayerSession("player-1", out _));
    }
}
""",
)

# Lock the redirect policy in a focused regression test.
replace_once(
    "tests/NzbWebDAV.Tests/Clients/MediaServers/MediaPlaybackSessionSourceTests.cs",
    """    [Fact]
    public void PlexParser_NormalizesSessionStateProgressAndPath()
""",
    """    [Fact]
    public void SessionHttpHandler_DoesNotAutomaticallyFollowRedirects()
    {
        using var handler = MediaServerHttp.CreateSessionHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void PlexParser_NormalizesSessionStateProgressAndPath()
""",
)

# Explicit support-pack coverage for the structured media-server secret.
replace_once(
    "tests/NzbWebDAV.Tests/Services/SupportPack/SupportPackRedactorTests.cs",
    """    [Fact]
    public void RedactText_StillRedactsSentinelSecretsInsideAllowlistedJson()
""",
    """    [Fact]
    public void RedactConfigurationValue_RedactsMediaServerTokens()
    {
        var redactor = new SupportPackRedactor([]);
        var result = redactor.RedactConfigurationValue(
            ConfigKeys.MediaServersInstances,
            """{"Instances":[{"Name":"Plex","BaseUrl":"http://plex.test","Token":"media-secret"}]}""");

        Assert.DoesNotContain("media-secret", result, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result);
        Assert.Equal(
            "[REDACTED]",
            document.RootElement.GetProperty("Instances")[0].GetProperty("Token").GetString());
    }

    [Fact]
    public void RedactText_StillRedactsSentinelSecretsInsideAllowlistedJson()
""",
)

print("Slice D staging patch applied.")
