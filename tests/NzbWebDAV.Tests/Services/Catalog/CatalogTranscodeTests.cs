using System.Net;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Catalog;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Services.Catalog;

/// <summary>
/// Compatibility-mode behavior: redirect validation, manifest shape, session expiry,
/// on-demand segment coalescing/retry, cancellation, and the cache bound's
/// active-reader rule.
/// </summary>
[Collection(nameof(CatalogTranscodeCollection))]
public sealed class CatalogTranscodeTests : IDisposable
{
    private readonly string _configPath =
        Path.Join(Path.GetTempPath(), $"nzbdav-transcode-tests-{Guid.NewGuid():N}");

    private readonly string? _previousConfigPath;
    private readonly string? _previousBackendUrl;

    public CatalogTranscodeTests()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        _previousBackendUrl = Environment.GetEnvironmentVariable("BACKEND_URL");
        Directory.CreateDirectory(_configPath);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);
        Environment.SetEnvironmentVariable("BACKEND_URL", "http://frontend:3000");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        Environment.SetEnvironmentVariable("BACKEND_URL", _previousBackendUrl);
        try
        {
            if (Directory.Exists(_configPath)) Directory.Delete(_configPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void RedirectNormalizationStripsThePublicPrefixAndKeepsTheQuery()
    {
        var session = NewSession(publicPathPrefix: "/nzbdav");

        var resolved = CatalogTranscodeService.BuildViewUri(
            session,
            new Uri("/nzbdav/view/abc?downloadKey=key&extension=mkv", UriKind.Relative));

        Assert.Equal("http://127.0.0.1:3000/view/abc?downloadKey=key&extension=mkv", resolved);
    }

    [Fact]
    public void RedirectNormalizationAcceptsUnprefixedViewPaths()
    {
        var session = NewSession(publicPathPrefix: "");

        var resolved = CatalogTranscodeService.BuildViewUri(
            session,
            new Uri("/view/abc?downloadKey=key", UriKind.Relative));

        Assert.Equal("http://127.0.0.1:3000/view/abc?downloadKey=key", resolved);
    }

    [Theory]
    [InlineData("https://evil.example/view/abc")]
    [InlineData("//evil.example/view/abc")]
    [InlineData("/admin/get-config")]
    [InlineData("/nzbdav/admin/get-config")]
    [InlineData("/view/../db.sqlite")]
    public void RedirectNormalizationRejectsEverythingElse(string location)
    {
        var session = NewSession(publicPathPrefix: "/nzbdav");
        var target = Uri.TryCreate(location, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(location, UriKind.Relative);

        var error = Assert.Throws<CatalogException>(() => CatalogTranscodeService.BuildViewUri(session, target));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(CatalogTranscodeService.RedirectRejectedMessage, error.SafeMessage);
    }

    [Fact]
    public void ManifestUsesSixSecondSegmentsWithContinuousTimestamps()
    {
        var manifest = CatalogHlsManifest.Build(TimeSpan.FromSeconds(12.5));
        var lines = manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", lines);
        Assert.Contains("#EXT-X-ENDLIST", lines);
        Assert.Contains("#EXTINF:6.000,", lines);
        Assert.Contains("#EXTINF:0.500,", lines);
        Assert.Equal(
            new[] { "segments/0.ts", "segments/1.ts", "segments/2.ts" },
            lines.Where(line => line.StartsWith("segments/", StringComparison.Ordinal)));

        // Secret blobs (a locally built public-domain film) are not needed for this assertion:
        // the segments are contiguous, so the manifest must not present a discontinuity.
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", lines);
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY-SEQUENCE", lines);
    }

    [Fact]
    public void RunUsesOneContinuousEncoderOnTheAbsoluteTimeline()
    {
        var arguments = FFmpegRunner.BuildRangeArguments(
            "http://127.0.0.1/view/movie.mkv?downloadKey=k",
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(24),
            4,
            "/tmp/run");

        // One encode spans the whole run: boundaries are cut by FFmpeg's own HLS muxer, so
        // audio and video meet exactly with no per-segment priming or re-quantisation.
        Assert.Equal("12", ValueAfter(arguments, "-ss"));
        Assert.Equal("24", ValueAfter(arguments, "-t"));
        Assert.Equal("hls", ValueAfter(arguments, "-f"));
        Assert.Equal("6", ValueAfter(arguments, "-hls_time"));
        Assert.Equal("event", ValueAfter(arguments, "-hls_playlist_type"));
        Assert.Equal("13", ValueAfter(arguments, "-output_ts_offset"));
        Assert.Equal("disabled", ValueAfter(arguments, "-avoid_negative_ts"));
        Assert.Equal("expr:gte(t,n_forced*6)", ValueAfter(arguments, "-force_key_frames"));

        // No per-segment trimming: cutting inside a continuous encode is what created holes.
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("atrim=", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("trim=", StringComparison.Ordinal));
    }

    private static string? ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index] == option) return arguments[index + 1];
        }
        return null;
    }

    [Fact]
    public async Task ExpiredSessionsAreRejected()
    {
        var clock = new ControllableTimeProvider(DateTimeOffset.UnixEpoch);
        var harness = CreateHarness(clock);
        var session = harness.AddSession();

        var playlist = await harness.Service.GetPlaylistAsync(session.Id, CancellationToken.None);
        Assert.StartsWith("#EXTM3U", playlist, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromHours(25));

        var error = await Assert.ThrowsAsync<CatalogException>(
            () => harness.Service.GetSegmentAsync(session.Id, 0, CancellationToken.None));
        Assert.Equal(404, error.StatusCode);
        Assert.Equal(CatalogTranscodeService.ExpiredSessionMessage, error.SafeMessage);
    }

    [Fact]
    public async Task SegmentIndicesOutsideTheManifestAreRejected()
    {
        var harness = CreateHarness(new ControllableTimeProvider(DateTimeOffset.UnixEpoch));
        var session = harness.AddSession();

        var negative = await Assert.ThrowsAsync<CatalogException>(
            () => harness.Service.GetSegmentAsync(session.Id, -1, CancellationToken.None));
        Assert.Equal(400, negative.StatusCode);

        // 12.5s of media → three segments (0, 1, 2).
        var beyond = await Assert.ThrowsAsync<CatalogException>(
            () => harness.Service.GetSegmentAsync(session.Id, 3, CancellationToken.None));
        Assert.Equal(404, beyond.StatusCode);
        Assert.Equal(CatalogTranscodeService.SegmentNotFoundMessage, beyond.SafeMessage);
        Assert.Equal(0, harness.Runner.SegmentCalls);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneSegmentProduceItOnce()
    {
        var harness = CreateHarness(new ControllableTimeProvider(DateTimeOffset.UnixEpoch));
        var session = harness.AddSession();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runner.OnSegment = async (_, _) => await gate.Task;

        var first = harness.Service.GetSegmentAsync(session.Id, 0, CancellationToken.None);
        var second = harness.Service.GetSegmentAsync(session.Id, 0, CancellationToken.None);
        await harness.Runner.SegmentStarted.Task;
        gate.SetResult();

        var leases = await Task.WhenAll(first, second);
        try
        {
            Assert.Equal(1, harness.Runner.SegmentCalls);
            Assert.Equal(leases[0].FilePath, leases[1].FilePath);
            Assert.True(File.Exists(leases[0].FilePath));
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    [Fact]
    public async Task FailedSegmentsAreRetriedByTheNextRequest()
    {
        var harness = CreateHarness(new ControllableTimeProvider(DateTimeOffset.UnixEpoch));
        var session = harness.AddSession();
        harness.Runner.OnSegment = (call, _) => call == 1
            ? throw CatalogException.Provider(CatalogTranscodeService.SegmentFailedMessage)
            : Task.CompletedTask;

        var failure = await Assert.ThrowsAsync<CatalogException>(
            () => harness.Service.GetSegmentAsync(session.Id, 0, CancellationToken.None));
        Assert.Equal(502, failure.StatusCode);

        using var lease = await harness.Service.GetSegmentAsync(session.Id, 0, CancellationToken.None);
        Assert.True(File.Exists(lease.FilePath));
        Assert.Equal(2, harness.Runner.SegmentCalls);
    }

    [Fact]
    public async Task CancellingARequestLeavesTheRunAliveForTheRetry()
    {
        var harness = CreateHarness(new ControllableTimeProvider(DateTimeOffset.UnixEpoch));
        var session = harness.AddSession();
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runner.OnSegment = async (_, _) => await gate.Task;

        var production = harness.Service.GetSegmentAsync(session.Id, 0, cancellation.Token);
        await harness.Runner.SegmentStarted.Task;
        await cancellation.CancelAsync();

        // The caller's cancellation detaches that waiter only; the shared run keeps going,
        // because another request (or the player's retry) still needs the same segment.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => production);

        var retry = harness.Service.GetSegmentAsync(session.Id, 0, CancellationToken.None);
        gate.SetResult();
        using var lease = await retry;
        Assert.True(File.Exists(lease.FilePath));
        Assert.Equal(1, harness.Runner.SegmentCalls);
    }

    [Fact]
    public async Task CacheBoundKeepsActiveSegmentsAndNeverExceedsTheBound()
    {
        var harness = CreateHarness(
            new ControllableTimeProvider(DateTimeOffset.UnixEpoch),
            maxCacheBytes: 1000);

        var session = harness.AddSession();
        var first = await harness.Service.GetSegmentAsync(session.Id, 0, CancellationToken.None);
        var firstPath = first.FilePath;
        Assert.True(File.Exists(firstPath));
        Assert.Equal(1000, harness.Service.CachedBytes);

        // A run publishes its whole lookahead at once. With only one segment of headroom the
        // extra segments cannot be cached while the first is still being read, so they are
        // served transiently and removed once their responses complete.
        var second = await harness.Service.GetSegmentAsync(session.Id, 1, CancellationToken.None);
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(second.FilePath));
        Assert.Equal(1000, harness.Service.CachedBytes);

        second.Dispose();
        Assert.False(File.Exists(second.FilePath));
        Assert.True(File.Exists(firstPath));

        // Closing the reader frees headroom; the bound is still never exceeded.
        first.Dispose();
        using var third = await harness.Service.GetSegmentAsync(session.Id, 2, CancellationToken.None);
        Assert.True(File.Exists(third.FilePath));
        Assert.True(harness.Service.CachedBytes <= 1000);
    }

    [Fact]
    public async Task ResolverRetriesWhileThePlayControllerIsPreparing()
    {
        var requests = 0;
        var handler = new ScriptedHandler((_, _) =>
        {
            requests++;
            if (requests == 1)
            {
                var preparing = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                preparing.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return preparing;
            }

            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("/view/abc?downloadKey=key&extension=mkv", UriKind.Relative);
            return redirect;
        });
        var harness = CreateHarness(new ControllableTimeProvider(DateTimeOffset.UnixEpoch), handler: handler);
        var session = harness.AddSession();

        var preparing = await Assert.ThrowsAsync<CatalogException>(
            () => harness.Service.GetPlaylistAsync(session.Id, CancellationToken.None));
        Assert.Equal(503, preparing.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), preparing.RetryAfter);

        var playlist = await harness.Service.GetPlaylistAsync(session.Id, CancellationToken.None);
        Assert.StartsWith("#EXTM3U", playlist, StringComparison.Ordinal);
        Assert.Equal(2, requests);
        Assert.Equal("http://127.0.0.1:3000/view/abc?downloadKey=key&extension=mkv", session.ResolvedViewUri);
    }

    [Fact]
    public async Task UnexpectedPlaybackResponsesSurfaceAsProviderFailures()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var harness = CreateHarness(new ControllableTimeProvider(DateTimeOffset.UnixEpoch), handler: handler);
        var session = harness.AddSession();

        var error = await Assert.ThrowsAsync<CatalogException>(
            () => harness.Service.GetPlaylistAsync(session.Id, CancellationToken.None));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("The playback backend returned an unexpected response.", error.SafeMessage);
    }

    [Fact]
    public async Task VideosWithoutADurationAreNotTranscodable()
    {
        var harness = CreateHarness(new ControllableTimeProvider(DateTimeOffset.UnixEpoch));
        harness.Runner.Duration = null;
        var session = harness.AddSession();

        var error = await Assert.ThrowsAsync<CatalogException>(
            () => harness.Service.GetPlaylistAsync(session.Id, CancellationToken.None));

        Assert.Equal(422, error.StatusCode);
        Assert.Equal(CatalogTranscodeService.NotPlayableMessage, error.SafeMessage);
    }

    [Fact]
    public async Task PruneRemovesExpiredSessionDirectories()
    {
        var clock = new ControllableTimeProvider(DateTimeOffset.UnixEpoch);
        var harness = CreateHarness(clock);
        var session = harness.AddSession();
        using (var lease = await harness.Service.GetSegmentAsync(session.Id, 0, CancellationToken.None))
        {
            Assert.True(File.Exists(lease.FilePath));
        }

        var directory = Path.Join(CatalogTranscodeService.CacheRoot, session.Id.ToString("N"));
        Assert.True(Directory.Exists(directory));

        harness.Service.Prune(DateTimeOffset.UnixEpoch.AddHours(25));

        Assert.False(Directory.Exists(directory));
        Assert.Equal(0, harness.Service.CachedBytes);
    }

    [Fact]
    public void FfmpegRunnerParsesContainerAndStreamDurations()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(12.5),
            FFmpegRunner.ParseDuration("""{"format":{"duration":"12.500000"}}"""));

        Assert.Equal(
            TimeSpan.FromSeconds(31),
            FFmpegRunner.ParseDuration(
                """{"format":{"duration":"N/A"},"streams":[{"codec_type":"video","duration":"31.000000"}]}"""));

        Assert.Null(FFmpegRunner.ParseDuration("""{"format":{"duration":"0"}}"""));
        Assert.Null(FFmpegRunner.ParseDuration("""{"format":{}}"""));
        Assert.Null(FFmpegRunner.ParseDuration("not json"));
    }

    private Harness CreateHarness(
        ControllableTimeProvider clock,
        long maxCacheBytes = CatalogTranscodeOptions.DefaultMaxCacheBytes,
        ScriptedHandler? handler = null)
    {
        var sessions = new CatalogPlaybackSessionStore(clock);
        var runner = new FakeFFmpegRunner();
        handler ??= new ScriptedHandler((_, _) =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("/view/abc?downloadKey=key&extension=mkv", UriKind.Relative);
            return redirect;
        });

        var service = new CatalogTranscodeService(
            runner,
            sessions,
            new StubHttpClientFactory(handler),
            new CatalogTranscodeOptions { MaxCacheBytes = maxCacheBytes, TimeProvider = clock });
        return new Harness(service, sessions, runner);
    }

    private static CatalogPlaybackSession NewSession(string publicPathPrefix)
        => new()
        {
            Id = Guid.NewGuid(),
            PublicDirectUrl = "http://localhost:3000/api/search/token/play/play.mkv",
            InternalDirectUrl = "http://127.0.0.1:8080/api/search/token/play/play.mkv",
            TitleType = "movie",
            TitleId = "tt0063350",
            ReleaseTitle = "Night.of.the.Living.Dead.1968.1080p.WEB-DL.x264",
            PublicPathPrefix = publicPathPrefix,
            LastAccess = DateTimeOffset.UnixEpoch,
        };

    private sealed class Harness(
        CatalogTranscodeService service,
        CatalogPlaybackSessionStore sessions,
        FakeFFmpegRunner runner)
    {
        public CatalogTranscodeService Service { get; } = service;
        public FakeFFmpegRunner Runner { get; } = runner;

        public CatalogPlaybackSession AddSession()
            => sessions.Add(
                "http://localhost:3000/api/search/token/play/play.mkv",
                "http://127.0.0.1:8080/api/search/token/play/play.mkv",
                "movie",
                "tt0063350",
                "Night.of.the.Living.Dead.1968.1080p.WEB-DL.x264",
                "");
    }

    private sealed class FakeFFmpegRunner : IFFmpegRunner
    {
        public TaskCompletionSource SegmentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan? Duration { get; set; } = TimeSpan.FromSeconds(12.5);

        public int ProbeCalls { get; private set; }

        /// <summary>Number of runs started (the old code counted one encode per segment).</summary>
        public int SegmentCalls { get; private set; }

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<int, CancellationToken, Task>? OnSegment { get; set; }

        public Task<TimeSpan?> ProbeDurationAsync(string sourceUri, CancellationToken ct)
        {
            ProbeCalls++;
            return Task.FromResult(Duration);
        }

        public async Task TranscodeRangeAsync(
            string sourceUri,
            TimeSpan start,
            int segmentCount,
            string outputDirectory,
            Action<int, string> onSegmentPublished,
            CancellationToken ct)
        {
            SegmentCalls++;
            SegmentStarted.TrySetResult();
            Directory.CreateDirectory(outputDirectory);
            try
            {
                if (OnSegment is { } hook) await hook(SegmentCalls, ct);
                for (var index = 0; index < segmentCount; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = Path.Join(outputDirectory, $"{index}.ts");
                    await File.WriteAllBytesAsync(path, new byte[1000], ct);
                    onSegmentPublished(index, path);
                }
            }
            catch (OperationCanceledException)
            {
                // Stands in for the production runner's process-tree kill.
                CancellationObserved.TrySetResult();
                throw;
            }
        }

    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

[CollectionDefinition(nameof(CatalogTranscodeCollection), DisableParallelization = true)]
public sealed class CatalogTranscodeCollection;
