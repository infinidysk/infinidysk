using Microsoft.AspNetCore.Http;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Tests.WebDav;

public class GetAndHeadHandlerRangeTests
{
    [Theory]
    [InlineData("npt=0.000-")]
    [InlineData("bytes=99999999999999999999-")]
    [InlineData("bytes=-")]
    [InlineData("bytes=0-1,5-9")]
    [InlineData("items=0-9")]
    [InlineData("")]
    public void TryResolveRange_IgnoresMalformedOrMultiRange(string header)
    {
        Assert.Null(GetAndHeadHandlerPatch.TryResolveRange(isHeadRequest: false, header));
    }

    [Fact]
    public void TryResolveRange_ParsesByteRange()
    {
        var range = GetAndHeadHandlerPatch.TryResolveRange(isHeadRequest: false, "bytes=0-499");
        Assert.NotNull(range);
        Assert.Equal(0L, range!.Start);
        Assert.Equal(499L, range.End);
    }

    [Fact]
    public void TryResolveRange_ParsesSuffixRange()
    {
        var range = GetAndHeadHandlerPatch.TryResolveRange(isHeadRequest: false, "bytes=-500");
        Assert.NotNull(range);
        Assert.Null(range!.Start);
        Assert.Equal(500L, range.End);
    }

    [Theory]
    [InlineData("bytes=0-0")]
    [InlineData("bytes=-500")]
    [InlineData("npt=0.000-")]
    public void TryResolveRange_IgnoresRangeOnHead(string header)
    {
        Assert.Null(GetAndHeadHandlerPatch.TryResolveRange(isHeadRequest: true, header));
    }

    [Fact]
    public async Task Head_KnownSizeFile_UsesMetadataWithoutOpeningStream()
    {
        var item = new CountingStoreItem(fileSize: 1234);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Head;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = "/movie.mkv";

        var handler = new GetAndHeadHandlerPatch(
            new SingleItemStore(item),
            new ConfigManager(),
            new ProviderUsageTracker(),
            new ActiveReadRegistry(),
            new ConcurrentReadTracker(),
            new StreamTraceBuffer(100, enabled: false),
            new StreamingFailureTracker(),
            new SharedStreamRegistry(new ConfigManager(), new ConcurrentReadTracker()));

        var handled = await handler.HandleRequestAsync(context);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1234, context.Response.ContentLength);
        Assert.Equal(0, item.OpenCount);
    }

    [Fact]
    public void CompletedFullRead_ClearsStreamingFailures()
    {
        var tracker = new StreamingFailureTracker();
        var item = NewDavItem();
        tracker.RecordFailure(item.Id);

        var cleared = GetAndHeadHandlerPatch.ClearStreamingFailureAfterCompletedRead(
            tracker, item, isHeadRequest: false, copySucceeded: true, copyStart: 0, copyEnd: null, streamLength: 100);

        Assert.True(cleared);
        Assert.Equal(0, tracker.GetFailureCount(item.Id));
    }

    [Fact]
    public void CompletedExplicitFullRange_ClearsStreamingFailures()
    {
        var tracker = new StreamingFailureTracker();
        var item = NewDavItem();
        tracker.RecordFailure(item.Id);

        var cleared = GetAndHeadHandlerPatch.ClearStreamingFailureAfterCompletedRead(
            tracker, item, isHeadRequest: false, copySucceeded: true, copyStart: 0, copyEnd: 99, streamLength: 100);

        Assert.True(cleared);
        Assert.Equal(0, tracker.GetFailureCount(item.Id));
    }

    [Fact]
    public void CompletedSuffixRange_DoesNotClearStreamingFailures()
    {
        var tracker = new StreamingFailureTracker();
        var item = NewDavItem();
        tracker.RecordFailure(item.Id);

        var cleared = GetAndHeadHandlerPatch.ClearStreamingFailureAfterCompletedRead(
            tracker, item, isHeadRequest: false, copySucceeded: true, copyStart: 50, copyEnd: 99, streamLength: 100);

        Assert.False(cleared);
        Assert.Equal(1, tracker.GetFailureCount(item.Id));
    }

    [Theory]
    [InlineData(true, false, 0, 99, 100)]
    [InlineData(false, false, 1, 99, 100)]
    [InlineData(false, false, 0, 98, 100)]
    [InlineData(false, true, 0, 99, 100)]
    public void IncompleteOrUnsuccessfulRead_DoesNotClearStreamingFailures(
        bool isHeadRequest,
        bool copyFailed,
        long copyStart,
        long copyEnd,
        long streamLength)
    {
        var tracker = new StreamingFailureTracker();
        var item = NewDavItem();
        tracker.RecordFailure(item.Id);

        var cleared = GetAndHeadHandlerPatch.ClearStreamingFailureAfterCompletedRead(
            tracker, item, isHeadRequest, !copyFailed, copyStart, copyEnd, streamLength);

        Assert.False(cleared);
        Assert.Equal(1, tracker.GetFailureCount(item.Id));
    }

    private static DavItem NewDavItem()
    {
        return new DavItem { Id = Guid.NewGuid() };
    }

    private sealed class CountingStoreItem(long fileSize) : BaseStoreReadonlyItem
    {
        public int OpenCount { get; private set; }
        public override string Name => "movie.mkv";
        public override string UniqueKey => "movie";
        public override long FileSize => fileSize;
        public override DateTime CreatedAt => DateTime.UnixEpoch;

        public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromResult(Stream.Null);
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

    [Fact]
    public void ThrowIfCopyEndedEarly_ThrowsWhenRangeEndsBeforePromisedLength()
    {
        using var src = new MemoryStream(new byte[1000]);
        var failure = Assert.Throws<IncompleteFileContentException>(() =>
            GetAndHeadHandlerPatch.ThrowIfCopyEndedEarly(
                bytesRemaining: 1024,
                rangeEnd: 499,
                rangeStart: 0,
                bytesDeliveredInRange: 256,
                filePath: "/content/movies/short.mkv",
                src));

        Assert.Equal(500, failure.ExpectedBytes);
        Assert.Equal(256, failure.DeliveredBytes);
        Assert.Contains("short.mkv", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfCopyEndedEarly_ThrowsWhenFullFileEndsBeforeDeclaredLength()
    {
        using var src = new MemoryStream(new byte[100]);
        var failure = Assert.Throws<IncompleteFileContentException>(() =>
            GetAndHeadHandlerPatch.ThrowIfCopyEndedEarly(
                bytesRemaining: long.MaxValue,
                rangeEnd: null,
                rangeStart: 0,
                bytesDeliveredInRange: 10,
                filePath: "/content/movies/short.mkv",
                src));

        Assert.Equal(100, failure.ExpectedBytes);
        Assert.Equal(10, failure.DeliveredBytes);
    }

    [Fact]
    public void ThrowIfCopyEndedEarly_AllowsNaturalEofOnFullFileGet()
    {
        using var src = new MemoryStream(new byte[100]);
        var ex = Record.Exception(() =>
            GetAndHeadHandlerPatch.ThrowIfCopyEndedEarly(
                bytesRemaining: long.MaxValue,
                rangeEnd: null,
                rangeStart: 0,
                bytesDeliveredInRange: 100,
                filePath: "/content/movies/full.mkv",
                src));

        Assert.Null(ex);
    }

    [Fact]
    public void ThrowIfCopyEndedEarly_AllowsCompletedRange()
    {
        using var src = new MemoryStream(new byte[1000]);
        var ex = Record.Exception(() =>
            GetAndHeadHandlerPatch.ThrowIfCopyEndedEarly(
                bytesRemaining: 0,
                rangeEnd: 499,
                rangeStart: 0,
                bytesDeliveredInRange: 500,
                filePath: "/content/movies/ok.mkv",
                src));

        Assert.Null(ex);
    }

    [Fact]
    public async Task WriteWithProgressTimeout_CompletesWhenClientReads()
    {
        using var readCts = new CancellationTokenSource();
        using var dest = new MemoryStream();

        await StreamingResponseWriteWatchdog.WriteWithProgressTimeoutAsync(
            dest, new byte[1024], TimeSpan.FromSeconds(5), readCts, CancellationToken.None);

        Assert.Equal(1024, dest.Length);
        Assert.False(readCts.IsCancellationRequested);
    }

    [Fact]
    public async Task WriteWithProgressTimeout_CancelsReadTokenWhenClientStalls()
    {
        using var readCts = new CancellationTokenSource();
        using var dest = new NeverCompletingWriteStream();

        var ex = await Assert.ThrowsAsync<NzbWebDAV.Exceptions.StreamingWriteTimeoutException>(async () =>
            await StreamingResponseWriteWatchdog.WriteWithProgressTimeoutAsync(
                dest, new byte[1024], TimeSpan.FromMilliseconds(50), readCts, CancellationToken.None));

        Assert.True(readCts.IsCancellationRequested);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task WriteWithProgressTimeout_ZeroTimeoutDisablesWatchdog()
    {
        using var readCts = new CancellationTokenSource();
        using var dest = new MemoryStream();

        await StreamingResponseWriteWatchdog.WriteWithProgressTimeoutAsync(
            dest, new byte[512], TimeSpan.Zero, readCts, CancellationToken.None);

        Assert.Equal(512, dest.Length);
        Assert.False(readCts.IsCancellationRequested);
    }

    private sealed class NeverCompletingWriteStream : Stream
    {
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Never complete: simulates a client that stopped reading but kept the
            // connection open. Honors cancellation so the test does not hang.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
    }
}
