using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(SharedStreamCollection))]
public class SharedStreamRegistryTests
{
    [Fact]
    public async Task ViewAndWebdavPaths_NormalizeToTheSameKey()
    {
        var (registry, tracker, source) = CreateRegistry();
        await using var registryDispose = registry;
        var payload = source.Data;

        await using var view = (await Attach(registry, source, "content/My Movie.mkv", 0)).Stream;
        await using var webdav = (await Attach(
            registry, source, Uri.UnescapeDataString("/content/My%20Movie.mkv"), 0)).Stream;

        Assert.Equal(1, tracker.Snapshot().SharedEntriesCreated);
        Assert.Equal(1, tracker.Snapshot().SharedAttachHits);
        Assert.Equal(payload, await ReadAllAsync(view));
        Assert.Equal(payload, await ReadAllAsync(webdav));
        Assert.Equal(
            tracker.Snapshot().SharedAttachHits + tracker.Snapshot().SharedAttachMisses,
            tracker.Snapshot().SharedAttachAttempts);
    }

    [Fact]
    public async Task SmallClosedRange_AttachesIfCovered_ButDoesNotCreate()
    {
        var config = Config(
            (ConfigKeys.UsenetSharedStreamsSmallRangeMaxMb, "1"),
            (ConfigKeys.UsenetSharedStreamsRingMb, "4"));
        var (registry, tracker, source) = CreateRegistry(config);
        await using var registryDispose = registry;

        var uncovered = await registry.TryAttachAsync(
            "/movie.mkv", 0, 1024, source.FileSize, source, NoFallback, CancellationToken.None);
        Assert.Null(uncovered);
        Assert.Equal(1, tracker.Snapshot().SharedAttachMissesSmallRangeNoEntry);
        Assert.Equal(0, tracker.Snapshot().SharedEntriesCreated);

        await using var full = (await Attach(registry, source, "/movie.mkv", 0, endOffset: null)).Stream;
        var small = await registry.TryAttachAsync(
            "/movie.mkv", 0, 1024, source.FileSize, source, NoFallback, CancellationToken.None);
        Assert.NotNull(small);
        await small!.Stream.DisposeAsync();
        Assert.Equal(1, tracker.Snapshot().SharedAttachHits);
        Assert.Equal(1, tracker.Snapshot().SharedEntriesCreated);
    }

    [Fact]
    public async Task OpeningEntry_CountsTowardCaps_AndIsNotAttachable()
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new MemoryStreamSource(Payload(), async _ =>
        {
            opened.TrySetResult();
            await release.Task;
        });
        var (registry, tracker, _) = CreateRegistry(source: source);
        await using var registryDispose = registry;

        var create = registry.TryAttachAsync(
            "/movie.mkv", 0, null, source.FileSize, source, NoFallback, CancellationToken.None);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, registry.LiveEntryCount);

        var concurrent = await registry.TryAttachAsync(
            "/movie.mkv", 0, null, source.FileSize, source, NoFallback, CancellationToken.None);
        Assert.Null(concurrent);
        Assert.Equal(1, tracker.Snapshot().SharedAttachMissesEntryUnusable);

        release.TrySetResult();
        var createdResult = await create;
        Assert.NotNull(createdResult);
        await using var created = createdResult!.Stream;
        Assert.NotNull(created);
    }

    [Fact]
    public async Task PerFileAndGlobalCaps_DeclineCreates()
    {
        var config = Config(
            (ConfigKeys.UsenetSharedStreamsMaxEntries, "1"),
            (ConfigKeys.UsenetSharedStreamsMaxEntriesPerFile, "1"),
            (ConfigKeys.UsenetSharedStreamsRingMb, "4"));
        var (registry, tracker, source) = CreateRegistry(config);
        await using var registryDispose = registry;

        await using var first = (await Attach(registry, source, "/a.mkv", 0)).Stream;
        var secondSame = await registry.TryAttachAsync(
            "/a.mkv", 5 * 1024 * 1024, null, source.FileSize, source, NoFallback, CancellationToken.None);
        Assert.Null(secondSame);
        Assert.True(
            tracker.Snapshot().SharedAttachMissesAtEntryCap
            + tracker.Snapshot().SharedAttachMissesAtGlobalCap
            + tracker.Snapshot().SharedAttachMissesAheadOfFrontier
            >= 1);

        var otherFile = await registry.TryAttachAsync(
            "/b.mkv", 0, null, source.FileSize, source, NoFallback, CancellationToken.None);
        Assert.Null(otherFile);
        Assert.True(tracker.Snapshot().SharedAttachMissesAtGlobalCap >= 1);
    }

    [Fact]
    public async Task AheadOfFrontier_IsAMiss()
    {
        var config = Config((ConfigKeys.UsenetSharedStreamsRingMb, "4"));
        var (registry, tracker, source) = CreateRegistry(config, payloadSize: 8 * 1024 * 1024);
        await using var registryDispose = registry;
        await using var first = (await Attach(registry, source, "/movie.mkv", 0)).Stream;
        var far = await registry.TryAttachAsync(
            "/movie.mkv",
            6 * 1024 * 1024,
            null,
            source.FileSize,
            source,
            NoFallback,
            CancellationToken.None);
        Assert.True(
            far is null
            || tracker.Snapshot().SharedAttachMissesAheadOfFrontier
            + tracker.Snapshot().SharedAttachMissesAtEntryCap > 0);
        if (far is not null)
            await far.Stream.DisposeAsync();
        Assert.Equal(
            tracker.Snapshot().SharedAttachHits + tracker.Snapshot().SharedAttachMisses,
            tracker.Snapshot().SharedAttachAttempts);
    }

    [Fact]
    public async Task OpenFailure_RemovesReservation_AndPropagates()
    {
        var source = new ThrowingSource();
        var tracker = new ConcurrentReadTracker();
        var registry = new SharedStreamRegistry(Config((ConfigKeys.UsenetSharedStreamsRingMb, "4")), tracker);
        await using var registryDispose = registry;

        await Assert.ThrowsAsync<IOException>(() => registry.TryAttachAsync(
            "/movie.mkv", 0, null, 16, source, NoFallback, CancellationToken.None));
        Assert.True(registry.IsEmpty);
        Assert.Equal(0, registry.LiveEntryCount);
    }

    [Fact]
    public async Task HitsPlusMisses_EqualsAttempts()
    {
        var (registry, tracker, source) = CreateRegistry();
        await using var registryDispose = registry;
        await using var first = (await Attach(registry, source, "/movie.mkv", 0)).Stream;
        await using var second = (await Attach(registry, source, "/movie.mkv", 0)).Stream;
        _ = await registry.TryAttachAsync(
            "/other.mkv", 0, 8, source.FileSize, source, NoFallback, CancellationToken.None);

        var snapshot = tracker.Snapshot();
        Assert.Equal(snapshot.SharedAttachHits + snapshot.SharedAttachMisses, snapshot.SharedAttachAttempts);
        Assert.True(snapshot.SharedAttachAttempts >= 3);
    }

    [Fact]
    public async Task RapidAttachDetachChurn_LeavesRegistryEmpty()
    {
        var clock = new ControllableTimeProvider();
        var config = Config(
            (ConfigKeys.UsenetSharedStreamsRingMb, "4"),
            (ConfigKeys.UsenetSharedStreamsGraceSeconds, "1"));
        var tracker = new ConcurrentReadTracker();
        var registry = new SharedStreamRegistry(config, tracker, clock);
        await using var registryDispose = registry;
        var source = new MemoryStreamSource(Payload());

        for (var i = 0; i < 50; i++)
        {
            var result = await Attach(registry, source, "/movie.mkv", 0);
            await using var stream = result.Stream;
            await stream.ReadAsync(new byte[1]);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => registry.IsEmpty);
        Assert.Equal(0, tracker.Snapshot().SharedStreamRingRetainedBytes);
        Assert.True(source.OpenCount >= 1);
        Assert.True(source.OpenCount <= 50);
    }

    [Fact]
    public async Task SaturatedBudgetChurn_ReclaimsEveryLease()
    {
        const int segmentSize = 64;
        var budget = new InFlightArticleBudget(segmentSize);
        using var held = await budget.LeaseAsync(segmentSize, CancellationToken.None);
        Assert.Equal(segmentSize, budget.LeasedBytes);

        var clock = new ControllableTimeProvider();
        var config = Config(
            (ConfigKeys.UsenetSharedStreamsRingMb, "4"),
            (ConfigKeys.UsenetSharedStreamsGraceSeconds, "1"));
        var tracker = new ConcurrentReadTracker();
        var registry = new SharedStreamRegistry(config, tracker, clock);
        await using var registryDispose = registry;
        var source = new NzbFileSource(segmentCount: 4, segmentSize: segmentSize, budget);

        for (var i = 0; i < 50; i++)
        {
            var result = await Attach(registry, source, "/movie.mkv", 0);
            await using var stream = result.Stream;
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => registry.IsEmpty);
        Assert.Equal(segmentSize, budget.LeasedBytes);
        held.Dispose();
        Assert.Equal(0, budget.LeasedBytes);
        Assert.Equal(0, tracker.Snapshot().SharedStreamRingRetainedBytes);
    }

    [Fact]
    public async Task Disabled_IsIneligibleAndDoesNotCreate()
    {
        var config = Config((ConfigKeys.UsenetSharedStreamsEnabled, "false"));
        var (registry, tracker, source) = CreateRegistry(config);
        await using var registryDispose = registry;
        Assert.Null(await registry.TryAttachAsync(
            "/movie.mkv", 0, null, source.FileSize, source, NoFallback, CancellationToken.None));
        Assert.Equal(1, tracker.Snapshot().SharedAttachMissesIneligible);
        Assert.Equal(0, tracker.Snapshot().SharedEntriesCreated);
        Assert.True(registry.IsEmpty);
    }

    private static async Task<SharedAttachResult> Attach(
        SharedStreamRegistry registry,
        IDetachedStreamSource source,
        string path,
        long start,
        long? endOffset = null)
    {
        var result = await registry.TryAttachAsync(
            path, start, endOffset, source.FileSize, source, NoFallback, CancellationToken.None);
        Assert.NotNull(result);
        return result!;
    }

    private static (SharedStreamRegistry Registry, ConcurrentReadTracker Tracker, MemoryStreamSource Source)
        CreateRegistry(
            ConfigManager? config = null,
            MemoryStreamSource? source = null,
            int payloadSize = 64 * 1024)
    {
        config ??= Config((ConfigKeys.UsenetSharedStreamsRingMb, "4"));
        var tracker = new ConcurrentReadTracker();
        var registry = new SharedStreamRegistry(config, tracker, TimeProvider.System);
        source ??= new MemoryStreamSource(Payload(payloadSize));
        return (registry, tracker, source);
    }

    private static ConfigManager Config(params (string Key, string Value)[] values)
    {
        var config = new ConfigManager();
        config.UpdateValues(
            values.Select(pair => new ConfigItem { ConfigName = pair.Key, ConfigValue = pair.Value }).ToList());
        return config;
    }

    private static byte[] Payload(int size = 64 * 1024)
    {
        var data = new byte[size];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 251);
        return data;
    }

    private static Task<Stream> NoFallback(long offset, CancellationToken _) =>
        throw new InvalidOperationException($"Fallback should not run at {offset}.");

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private sealed class MemoryStreamSource(
        byte[] data,
        Func<CancellationToken, Task>? beforeOpen = null) : IDetachedStreamSource
    {
        public byte[] Data => data;
        public long FileSize => data.Length;
        public int OpenCount;

        public async Task<DetachedStreamLease> GetDetachedReadableStreamAsync(CancellationToken cancellationToken)
        {
            if (beforeOpen is not null)
                await beforeOpen(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref OpenCount);
            return new DetachedStreamLease
            {
                Stream = new MemoryStream(data, writable: false),
                Ownership = NullAsyncDisposable.Instance,
            };
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private sealed class NzbFileSource(
        int segmentCount,
        int segmentSize,
        InFlightArticleBudget budget) : IDetachedStreamSource
    {
        public long FileSize => (long)segmentCount * segmentSize;

        public Task<DetachedStreamLease> GetDetachedReadableStreamAsync(CancellationToken cancellationToken)
        {
            var payload = new byte[FileSize];
            var ids = Enumerable.Range(0, segmentCount).Select(i => $"seg-{i}").ToArray();
            var segments = new Dictionary<string, byte[]>();
            var ranges = new Dictionary<string, LongRange>();
            var longRanges = new LongRange[segmentCount];
            for (var i = 0; i < segmentCount; i++)
            {
                var start = i * segmentSize;
                segments[ids[i]] = payload.AsSpan(start, segmentSize).ToArray();
                ranges[ids[i]] = new LongRange(start, start + segmentSize);
                longRanges[i] = new LongRange(start, start + segmentSize);
            }

            var client = new FakeNntpClient(
                segments,
                useCachedYencStreams: true,
                segmentRanges: ranges);
            var stream = new NzbFileStream(
                ids,
                payload.Length,
                client,
                articleBufferSize: 4,
                segmentByteRanges: longRanges,
                inFlightArticleBudget: budget);
            return Task.FromResult(new DetachedStreamLease
            {
                Stream = stream,
                Ownership = NullAsyncDisposable.Instance,
            });
        }
    }

    private sealed class ThrowingSource : IDetachedStreamSource
    {
        public long FileSize => 16;
        public Task<DetachedStreamLease> GetDetachedReadableStreamAsync(CancellationToken cancellationToken) =>
            throw new IOException("open failed");
    }
}
