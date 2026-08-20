using System.IO;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.WebDav.Base;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class MultiProviderNntpClientTerminalMissLoggingTests
{
    private const string MissTemplate =
        "Usenet segment was unavailable from all eligible provider sources";

    [Fact]
    public async Task TwoProvidersReturn430_EmitsOneWarning()
    {
        const string segmentId = "two-430@terminal-miss";
        var events = await CaptureLogsAsync(async () =>
        {
            using var client = TwoMissingProviders(out _);
            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.Equal(UsenetResponseType.NoArticleWithThatMessageId, response.ResponseType);
        });

        var warning = Assert.Single(events, e => IsMissWarningFor(e, segmentId));
        Assert.Equal("body", PropertyText(warning, "Operation"));
    }

    [Fact]
    public async Task TwoProvidersThrowNotFound_EmitsOneWarning()
    {
        const string segmentId = "two-throw@terminal-miss";
        var events = await CaptureLogsAsync(async () =>
        {
            using var client = TwoThrowingProviders();
            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
                () => client.DecodedBodyAsync(segmentId, CancellationToken.None));
        });

        Assert.Single(events, e => IsMissWarningFor(e, segmentId));
    }

    [Fact]
    public async Task Definitive451_FollowsTheSamePolicy()
    {
        const string segmentId = "two-451@terminal-miss";
        var events = await CaptureLogsAsync(async () =>
        {
            using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(
                    new MultiProviderNntpClientTests.ScriptedNntpClient
                    {
                        BatchResponseCode = UsenetArticleAvailability.ArticleUnavailable,
                        SingularResponseCode = UsenetArticleAvailability.ArticleUnavailable,
                    },
                    host: "a.example"),
                MultiProviderNntpClientTests.CreateProvider(
                    new MultiProviderNntpClientTests.ScriptedNntpClient
                    {
                        BatchResponseCode = UsenetArticleAvailability.ArticleUnavailable,
                        SingularResponseCode = UsenetArticleAvailability.ArticleUnavailable,
                    },
                    host: "b.example"),
            ]);
            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.Equal(UsenetArticleAvailability.ArticleUnavailable, response.ResponseCode);
        });

        Assert.Single(events, e => IsMissWarningFor(e, segmentId));
    }

    [Fact]
    public async Task CallbackStreamingAllMiss_EmitsOneWarningAndOneCallback()
    {
        const string segmentId = "callback-miss@terminal-miss";
        var callbacks = 0;
        var events = await CaptureLogsAsync(async () =>
        {
            using var client = TwoThrowingProviders();
            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
                client.DecodedBodyAsync(
                    segmentId,
                    (_, _) => Interlocked.Increment(ref callbacks),
                    CancellationToken.None));
        });

        Assert.Single(events, e => IsMissWarningFor(e, segmentId));
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public async Task BatchTerminalMiss_EmitsOneWarningAndCompletesInOrder()
    {
        const string segmentId = "batch-miss@terminal-miss";
        var events = await CaptureLogsAsync(async () =>
        {
            using var client = TwoMissingProviders(out _);
            var batch = await client.DecodedBodiesAsync(
                [segmentId],
                onConnectionReadyAgain: null,
                CancellationToken.None);
            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() => batch.Responses[0]);
        });

        Assert.Single(events, e => IsMissWarningFor(e, segmentId));
    }

    [Fact]
    public async Task PersistentMissCacheSkipsEveryProvider_EmitsOneWarningWithZeroAttempts()
    {
        const string segmentId = "cache-skip@terminal-miss";
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
        cache.MarkMissing(ArticleMissNegativeCache.BuildKey(segmentId, "a.example", ""));
        cache.MarkMissing(ArticleMissNegativeCache.BuildKey(segmentId, "b.example", ""));
        var first = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 430,
        };
        var second = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 430,
        };

        var events = await CaptureLogsAsync(async () =>
        {
            using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(first, host: "a.example"),
                MultiProviderNntpClientTests.CreateProvider(second, host: "b.example"),
            ], articleMissCache: cache);

            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
                () => client.DecodedBodyAsync(segmentId, CancellationToken.None));
        });

        Assert.Equal(0, first.SingularRequests);
        Assert.Equal(0, second.SingularRequests);
        var warning = Assert.Single(events, e => IsMissWarningFor(e, segmentId));
        Assert.Contains("Attempts: {Attempts}", warning.MessageTemplate.Text, StringComparison.Ordinal);
        Assert.Equal("0", PropertyText(warning, "Attempts"));
    }

    [Fact]
    public async Task StorageGroupSiblingSkip_IsCountedOnTheTerminalWarning()
    {
        const string segmentId = "storage-group@terminal-miss";
        var events = await CaptureLogsAsync(async () =>
        {
            using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(
                    MissingClient(), host: "a.example", storageGroup: "block-1"),
                MultiProviderNntpClientTests.CreateProvider(
                    MissingClient(), host: "b.example", storageGroup: "block-1"),
            ]);
            await client.DecodedBodyAsync(segmentId, CancellationToken.None);
        });

        var warning = Assert.Single(events, e => IsMissWarningFor(e, segmentId));
        Assert.Equal("1", PropertyText(warning, "StorageGroupSkips"));
        Assert.Equal("1", PropertyText(warning, "Attempts"));
    }

    [Fact]
    public async Task MissPlusNetworkError_DoesNotEmitPureMissWarning()
    {
        const string segmentId = "mixed-network@terminal-miss";
        var events = await CaptureLogsAsync(async () =>
        {
            using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(MissingClient(), host: "a.example"),
                MultiProviderNntpClientTests.CreateProvider(
                    new MultiProviderNntpClientTests.ScriptedNntpClient
                    {
                        BatchResponseCode = 222,
                        SingularResponseCode = 222,
                        SingularException = _ => new IOException("connection reset"),
                    },
                    host: "b.example"),
            ]);
            await Assert.ThrowsAsync<IOException>(
                () => client.DecodedBodyAsync(segmentId, CancellationToken.None));
        });

        Assert.DoesNotContain(events, e => IsMissWarningFor(e, segmentId));
        Assert.Contains(events, e =>
            e.Level == LogEventLevel.Warning
            && e.MessageTemplate.Text.Contains("All providers exhausted", StringComparison.Ordinal)
            && e.RenderMessage().Contains("connection reset", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationAfterAnEarlierMiss_DoesNotEmitTerminalMissWarning()
    {
        const string segmentId = "cancel-after-miss@terminal-miss";
        using var cts = new CancellationTokenSource();
        var events = await CaptureLogsAsync(async () =>
        {
            using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(MissingClient(), host: "a.example"),
                MultiProviderNntpClientTests.CreateProvider(
                    new MultiProviderNntpClientTests.ScriptedNntpClient
                    {
                        BatchResponseCode = 222,
                        SingularResponseCode = 222,
                        SingularException = _ =>
                        {
                            cts.Cancel();
                            throw new OperationCanceledException(cts.Token);
                        },
                    },
                    host: "b.example"),
            ]);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.DecodedBodyAsync(segmentId, cts.Token));
        });

        Assert.DoesNotContain(events, e => IsMissWarningFor(e, segmentId));
    }

    [Fact]
    public async Task NoConfiguredProviders_DoesNotEmitPureMissWarning()
    {
        const string segmentId = "no-providers@terminal-miss";
        var events = await CaptureLogsAsync(async () =>
        {
            using var client = new MultiProviderNntpClient([]);
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DecodedBodyAsync(segmentId, CancellationToken.None));
            Assert.Contains("no usenet providers", thrown.Message, StringComparison.OrdinalIgnoreCase);
        });

        Assert.DoesNotContain(events, e => IsMissWarningFor(e, segmentId));
    }

    [Fact]
    public async Task Unexpected400Response_DoesNotEmitPureMissWarning()
    {
        const string segmentId = "unexpected-400@terminal-miss";
        var events = await CaptureLogsAsync(async () =>
        {
            using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(
                    new MultiProviderNntpClientTests.ScriptedNntpClient
                    {
                        BatchResponseCode = 400,
                        SingularResponseCode = 400,
                    }),
            ]);
            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.Equal(400, response.ResponseCode);
        });

        Assert.DoesNotContain(events, e => IsMissWarningFor(e, segmentId));
    }

    [Fact]
    public async Task SharedPumpMiss_IncludesFileAttribution()
    {
        const string segmentId = "shared-pump@terminal-miss";
        var events = await CaptureLogsAsync(async () =>
        {
            using var nntp = TwoThrowingProviders();
            await using var upstream = MultiSegmentStream.Create(
                new[] { segmentId }.AsMemory(),
                nntp,
                articleBufferSize: 0,
                estimatedSegmentSize: 8,
                failFastOnFirstSegment: false,
                usePipelinedBodyRequests: false,
                CancellationToken.None,
                fileName: "movie.mkv",
                exactSegmentSizes: new long[] { 8 });
            await using var entry = new SharedStreamEntry(
                "/content/movie.mkv",
                0,
                8,
                64,
                TimeSpan.FromSeconds(10),
                CancellationToken.None,
                chunkSize: 8,
                leadBytes: 8);
            entry.BindAndStart(new DetachedStreamLease
            {
                Stream = upstream,
                Ownership = NullAsyncDisposable.Instance,
            });
            var reader = entry.TryAttach(0, NoFallback, out var reason);
            Assert.True(reader is not null, $"attach missed: {reason}");
            var attached = reader!;
            await using (attached)
            {
                var buffer = new byte[8];
                _ = await attached.ReadAsync(buffer);
            }
        });

        var warning = Assert.Single(events, e => IsMissWarningFor(e, segmentId));
        Assert.Equal("movie.mkv", PropertyText(warning, "FileName"));
    }

    [Fact]
    public async Task PlaybackZeroFill_DoesNotDuplicateTheTerminalMissWarning()
    {
        const string segmentId = "zero-fill@terminal-miss";
        var events = await CaptureLogsAsync(async () =>
        {
            using var nntp = TwoThrowingProviders();
            await using var stream = MultiSegmentStream.Create(
                new[] { segmentId }.AsMemory(),
                nntp,
                articleBufferSize: 0,
                estimatedSegmentSize: 8,
                failFastOnFirstSegment: false,
                usePipelinedBodyRequests: false,
                CancellationToken.None,
                fileName: "movie.mkv",
                exactSegmentSizes: new long[] { 8 });
            var buffer = new byte[8];
            _ = await stream.ReadAsync(buffer);
        });

        Assert.Single(events, e => IsMissWarningFor(e, segmentId));
    }

    private static MultiProviderNntpClient TwoMissingProviders(
        out MultiProviderNntpClientTests.ScriptedNntpClient first)
    {
        first = MissingClient();
        return new MultiProviderNntpClient(
        [
            MultiProviderNntpClientTests.CreateProvider(first, host: "a.example"),
            MultiProviderNntpClientTests.CreateProvider(MissingClient(), host: "b.example"),
        ]);
    }

    private static MultiProviderNntpClient TwoThrowingProviders() =>
        new(
        [
            MultiProviderNntpClientTests.CreateProvider(ThrowingMissingClient(), host: "a.example"),
            MultiProviderNntpClientTests.CreateProvider(ThrowingMissingClient(), host: "b.example"),
        ]);

    private static MultiProviderNntpClientTests.ScriptedNntpClient MissingClient() => new()
    {
        BatchResponseCode = 430,
        SingularResponseCode = 430,
    };

    private static MultiProviderNntpClientTests.ScriptedNntpClient ThrowingMissingClient() => new()
    {
        BatchResponseCode = 430,
        SingularResponseCode = 430,
        SingularException = id => new UsenetArticleNotFoundException(id),
    };

    private static Task<Stream> NoFallback(long offset, CancellationToken _) =>
        throw new InvalidOperationException($"Private fallback should not run at offset {offset}.");

    private static string PropertyText(LogEvent logEvent, string name)
    {
        if (!logEvent.Properties.TryGetValue(name, out var value))
            return "";
        return value is ScalarValue { Value: { } raw }
            ? raw.ToString() ?? ""
            : value.ToString();
    }

    private static bool IsMissWarning(LogEvent logEvent) =>
        logEvent.Level == LogEventLevel.Warning
        && logEvent.MessageTemplate.Text.Contains(MissTemplate, StringComparison.Ordinal);

    private static bool IsMissWarningFor(LogEvent logEvent, string segmentId) =>
        IsMissWarning(logEvent) && PropertyText(logEvent, "SegmentId") == segmentId;

    private static async Task<IReadOnlyList<LogEvent>> CaptureLogsAsync(Func<Task> act)
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            await act().ConfigureAwait(false);
        }
        finally
        {
            Log.Logger = previous;
        }

        return sink.Events;
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events) return _events.ToArray();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }
}
