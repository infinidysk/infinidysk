using Microsoft.AspNetCore.Http;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Tests.WebDav;

[Collection(nameof(SharedStreamCollection))]
public class SharedStreamHandlerTests
{
    [Fact]
    public async Task WebDavHead_NeverAttaches()
    {
        var item = new DetachedStoreItem(payload: [1, 2, 3, 4]);
        var (handler, _, _) = Handler(item);
        var context = Request(HttpMethods.Head, "/movie.mkv");

        await handler.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, item.DetachedOpenCount);
        Assert.Equal(0, item.PrivateOpenCount);
    }

    [Fact]
    public void ViewHead_IsNotEligibleForAttach()
    {
        var item = new DetachedStoreItem(payload: [1, 2, 3]);
        Assert.False(GetWebdavItemController.ShouldAttemptSharedAttach(HttpMethods.Head, item));
        Assert.True(GetWebdavItemController.ShouldAttemptSharedAttach(HttpMethods.Get, item));
        Assert.False(GetWebdavItemController.ShouldAttemptSharedAttach(HttpMethods.Get, new PrivateOnlyItem()));
    }

    [Fact]
    public async Task UnsatisfiableRange_Is416BeforeAttach()
    {
        var item = new DetachedStoreItem(payload: [1, 2, 3, 4]);
        var (handler, _, _) = Handler(item);
        var context = Request(HttpMethods.Get, "/movie.mkv", range: "bytes=99-120");
        using var body = new MemoryStream();
        context.Response.Body = body;

        await handler.HandleRequestAsync(context);

        Assert.Equal(416, context.Response.StatusCode);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges);
        Assert.Equal("bytes */4", context.Response.Headers.ContentRange);
        Assert.Equal(0, item.DetachedOpenCount);
        Assert.Equal(0, item.PrivateOpenCount);
    }

    [Fact]
    public async Task AttachHit_SetsDavItemOnHttpContext()
    {
        var davItem = new DavItem { Id = Guid.NewGuid(), Name = "movie.mkv" };
        var item = new DetachedStoreItem(payload: [10, 20, 30, 40], davItem);
        var failureTracker = new StreamingFailureTracker();
        failureTracker.RecordFailure(davItem.Id);
        var (handler, _, _) = Handler(item, failureTracker);
        var context = Request(HttpMethods.Get, "/movie.mkv");
        using var body = new MemoryStream();
        context.Response.Body = body;

        await handler.HandleRequestAsync(context);

        Assert.Same(davItem, context.Items["DavItem"]);
        Assert.Equal(1, item.DetachedOpenCount);
        Assert.Equal(0, item.PrivateOpenCount);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, body.ToArray());
        Assert.Equal(0, failureTracker.GetFailureCount(davItem.Id));
    }

    [Fact]
    public async Task SuffixRange_AttachesAtResolvedStartWhenCovered()
    {
        var payload = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();
        var item = new DetachedStoreItem(payload);
        var (handler, registry, _) = Handler(item);
        var pinned = await registry.TryAttachAsync(
            "/movie.mkv", 0, null, payload.Length, item, NoFallback, CancellationToken.None);
        Assert.NotNull(pinned);
        await using var pin = pinned!.Stream;

        var suffix = Request(HttpMethods.Get, "/movie.mkv", range: "bytes=-4");
        using var body = new MemoryStream();
        suffix.Response.Body = body;
        await handler.HandleRequestAsync(suffix);

        Assert.Equal(StatusCodes.Status206PartialContent, suffix.Response.StatusCode);
        Assert.Equal(payload[^4..], body.ToArray());
        Assert.Equal(1, item.DetachedOpenCount);
        Assert.Equal(0, item.PrivateOpenCount);
        Assert.Equal(payload.Length - 4, GetAndHeadHandlerPatch.ResolveAttachRange(
            GetAndHeadHandlerPatch.TryResolveRange(false, "bytes=-4"), payload.Length).Start);
    }

    [Fact]
    public async Task DetachedExactRange_UsesEntryReadAheadInsteadOfReaderRangeBudget()
    {
        const int segmentSize = 8;
        const int segmentCount = 8;
        var previous = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(segmentSize);
        try
        {
            var source = new ExactNzbFileSource(segmentCount, segmentSize);
            var (_, registry, _) = Handler(source);
            var created = await registry.TryAttachAsync(
                "/movie.mkv", 0, null, source.FileSize, source, NoFallback, CancellationToken.None);
            Assert.NotNull(created);
            await using var first = created!.Stream;
            var firstBuf = new byte[segmentSize];
            Assert.Equal(segmentSize, await first.ReadAsync(firstBuf));

            var overlapping = await registry.TryAttachAsync(
                "/movie.mkv", segmentSize, (segmentSize * 4) - 1, source.FileSize, source, NoFallback,
                CancellationToken.None);
            Assert.NotNull(overlapping);
            await using var second = overlapping!.Stream;
            var secondBuf = new byte[segmentSize * 3];
            var read = 0;
            while (read < secondBuf.Length)
            {
                var n = await second.ReadAsync(secondBuf.AsMemory(read));
                if (n == 0) break;
                read += n;
            }

            Assert.Equal(segmentSize * 3, read);
            Assert.Equal(source.Payload.AsSpan(segmentSize, read).ToArray(), secondBuf);
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previous);
        }
    }

    [Fact]
    public async Task SmallSuffixRange_DoesNotCreateAnEntry()
    {
        var payload = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();
        var item = new DetachedStoreItem(payload);
        var (handler, _, tracker) = Handler(item);
        var context = Request(HttpMethods.Get, "/movie.mkv", range: "bytes=-4");
        using var body = new MemoryStream();
        context.Response.Body = body;

        await handler.HandleRequestAsync(context);

        Assert.Equal(0, item.DetachedOpenCount);
        Assert.Equal(1, item.PrivateOpenCount);
        Assert.Equal(1, tracker.Snapshot().SharedAttachMissesSmallRangeNoEntry);
        Assert.Equal(payload[^4..], body.ToArray());
    }

    [Fact]
    public async Task LimitLength_WrappedSharedReader_IsByteExact()
    {
        var payload = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var item = new DetachedStoreItem(payload);
        var config = new ConfigManager();
        var tracker = new ConcurrentReadTracker();
        await using var registry = new SharedStreamRegistry(config, tracker);
        var attached = await registry.TryAttachAsync(
            "/movie.mkv", 8, null, payload.Length, item, NoFallback, CancellationToken.None);
        Assert.NotNull(attached);
        await using var limited = attached!.Stream.LimitLength(8);

        var buffer = new byte[32];
        var read = await limited.ReadAsync(buffer);
        Assert.Equal(8, read);
        Assert.Equal(payload.AsSpan(8, 8).ToArray(), buffer.AsSpan(0, 8).ToArray());
        Assert.Equal(0, await limited.ReadAsync(buffer));
    }

    [Fact]
    public async Task IneligibleItem_UsesPrivatePath()
    {
        var item = new PrivateOnlyItem();
        var (handler, _, _) = Handler(item);
        var context = Request(HttpMethods.Get, "/movie.mkv");
        using var body = new MemoryStream();
        context.Response.Body = body;

        await handler.HandleRequestAsync(context);

        Assert.Equal(1, item.PrivateOpenCount);
        Assert.Equal(new byte[] { 7, 8, 9 }, body.ToArray());
    }

    private static (GetAndHeadHandlerPatch Handler, SharedStreamRegistry Registry, ConcurrentReadTracker Tracker)
        Handler(IStoreItem item, StreamingFailureTracker? failures = null)
    {
        var config = new ConfigManager();
        var tracker = new ConcurrentReadTracker();
        var registry = new SharedStreamRegistry(config, tracker);
        var handler = new GetAndHeadHandlerPatch(
            new SingleItemStore(item),
            config,
            new ProviderUsageTracker(),
            new ActiveReadRegistry(),
            tracker,
            new StreamTraceBuffer(100, enabled: false),
            failures ?? new StreamingFailureTracker(),
            registry);
        return (handler, registry, tracker);
    }

    private static DefaultHttpContext Request(string method, string path, string? range = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = path;
        if (range is not null)
            context.Request.Headers.Range = range;
        return context;
    }

    private static Task<Stream> NoFallback(long offset, CancellationToken _) =>
        throw new InvalidOperationException($"Fallback should not run at {offset}.");

    private sealed class DetachedStoreItem(byte[] payload, DavItem? davItem = null)
        : BaseStoreReadonlyItem, IDetachedStreamSource
    {
        public int DetachedOpenCount;
        public int PrivateOpenCount;
        public override string Name => "movie.mkv";
        public override string UniqueKey => "movie";
        public override long FileSize => payload.Length;
        public override DateTime CreatedAt => DateTime.UnixEpoch;

        public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PrivateOpenCount);
            return Task.FromResult(TestStreams.Create(payload));
        }

        public Task<DetachedStreamLease> GetDetachedReadableStreamAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DetachedOpenCount);
            return Task.FromResult(new DetachedStreamLease
            {
                Stream = TestStreams.Create(payload),
                Ownership = NullAsyncDisposable.Instance,
                DavItem = davItem,
            });
        }
    }

    private sealed class PrivateOnlyItem : BaseStoreReadonlyItem
    {
        public int PrivateOpenCount;
        public override string Name => "movie.mkv";
        public override string UniqueKey => "private";
        public override long FileSize => 3;
        public override DateTime CreatedAt => DateTime.UnixEpoch;

        public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PrivateOpenCount);
            return Task.FromResult(TestStreams.Create([7, 8, 9]));
        }
    }

    private sealed class ExactNzbFileSource(int segmentCount, int segmentSize)
        : BaseStoreReadonlyItem, IDetachedStreamSource
    {
        public byte[] Payload { get; } = CreatePayload(segmentCount, segmentSize);
        public override string Name => "movie.mkv";
        public override string UniqueKey => "exact-nzb";
        public override long FileSize => Payload.Length;
        public override DateTime CreatedAt => DateTime.UnixEpoch;

        public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CreateStream());

        public Task<DetachedStreamLease> GetDetachedReadableStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DetachedStreamLease
            {
                Stream = CreateStream(),
                Ownership = NullAsyncDisposable.Instance,
            });

        private Stream CreateStream()
        {
            var ids = Enumerable.Range(0, segmentCount).Select(i => $"seg-{i}").ToArray();
            var segments = new Dictionary<string, byte[]>();
            var rangesById = new Dictionary<string, LongRange>();
            var ranges = new LongRange[segmentCount];
            for (var i = 0; i < segmentCount; i++)
            {
                var start = i * segmentSize;
                var slice = Payload.AsSpan(start, segmentSize).ToArray();
                segments[ids[i]] = slice;
                ranges[i] = new LongRange(start, start + segmentSize);
                rangesById[ids[i]] = ranges[i];
            }

            var client = new FakeNntpClient(segments, useCachedYencStreams: true, rangesById);
            return new NzbFileStream(
                ids,
                Payload.Length,
                client,
                articleBufferSize: 4,
                segmentByteRanges: ranges);
        }

        private static byte[] CreatePayload(int count, int size)
        {
            var data = new byte[count * size];
            for (var i = 0; i < data.Length; i++)
                data[i] = (byte)(i % 251);
            return data;
        }
    }

    private sealed class SingleItemStore(IStoreItem item) : IStore
    {
        public Task<IStoreItem?> GetItemAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult<IStoreItem?>(item);

        public Task<IStoreItem?> GetItemAsync(Uri uri, CancellationToken cancellationToken)
            => Task.FromResult<IStoreItem?>(item);

        public Task<IStoreCollection?> GetCollectionAsync(Uri uri, CancellationToken cancellationToken)
            => Task.FromResult<IStoreCollection?>(null);
    }
}
