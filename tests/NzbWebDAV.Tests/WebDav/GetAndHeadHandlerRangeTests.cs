using Microsoft.AspNetCore.Http;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Services;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Tests.WebDav;

[Collection(nameof(GlobalLoggerCollection))]
public class GetAndHeadHandlerRangeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_RangeTraceCountsOnlyItsOwnWrites(bool overlap)
    {
        var trace = new StreamTraceBuffer(100);
        var registry = new ActiveReadRegistry();
        var config = new ConfigManager();
        using var firstBody = new GatedWriteStream(overlap);
        using var secondBody = new MemoryStream();
        using var shared = new SharedStreamRegistry(config, new ConcurrentReadTracker());
        var handler = new GetAndHeadHandlerPatch(
            new SingleItemStore(new MemoryStoreItem()), config, new ProviderUsageTracker(),
            registry, new ConcurrentReadTracker(), trace, new StreamingFailureTracker(), shared);
        var middleware = new WebDavObservabilityMiddleware(async context =>
            await handler.HandleRequestAsync(context), trace);
        var first = NewGetContext("bytes=0-99", firstBody);
        var second = NewGetContext("bytes=100-299", secondBody);

        var firstRequest = middleware.InvokeAsync(first);
        try
        {
            if (overlap)
                await firstBody.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await firstRequest;
            await middleware.InvokeAsync(second);
        }
        finally
        {
            firstBody.Release.TrySetResult();
            await firstRequest;
        }

        var session = Assert.Single(trace.ListSessions());
        var events = trace.GetSessionEvents(session.SessionId);
        foreach (var opened in events.Where(entry => entry.Kind == "RangeOpen"))
        {
            var ended = Assert.Single(events, entry =>
                entry.Kind == "RangeEnd" && entry.RangeGeneration == opened.RangeGeneration);
            Assert.Equal(opened.RangeEnd - opened.RangeStart + 1, ended.BytesServed);
            var requestEnd = Assert.Single(events, entry =>
                entry.Kind == "RequestEnd" && entry.RangeGeneration == opened.RangeGeneration);
            Assert.NotNull(requestEnd.FirstByteMs);
            var requestDurationMs = requestEnd.RequestDurationMs;
            var firstByteMs = requestEnd.FirstByteMs;
            Assert.True(requestDurationMs >= firstByteMs);
            Assert.True(requestEnd.CleanupMs >= 0);
            Assert.Null(requestEnd.CancelledAtMs);
        }
        Assert.Equal(300, registry.GetBytesRead(session.SessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_InterruptedWriteCountsOnlySuccessfulBytes(bool cancelled)
    {
        var trace = new StreamTraceBuffer(100);
        var config = new ConfigManager();
        using var cancellation = new CancellationTokenSource();
        using var body = new InterruptedWriteStream(cancelled, cancellation);
        using var shared = new SharedStreamRegistry(config, new ConcurrentReadTracker());
        var handler = new GetAndHeadHandlerPatch(
            new SingleItemStore(new MemoryStoreItem(128)), config, new ProviderUsageTracker(),
            new ActiveReadRegistry(), new ConcurrentReadTracker(), trace, new StreamingFailureTracker(), shared);
        var middleware = new WebDavObservabilityMiddleware(async context =>
            await handler.HandleRequestAsync(context), trace);
        var context = NewGetContext("bytes=0-511", body);
        context.RequestAborted = cancellation.Token;

        var exception = await Record.ExceptionAsync(() => middleware.InvokeAsync(context));

        Assert.NotNull(exception);
        var events = trace.GetSessionEvents(Assert.Single(trace.ListSessions()).SessionId);
        var ended = Assert.Single(events, entry => entry.Kind == "RangeEnd");
        Assert.Equal(128, ended.BytesServed);
        Assert.Equal(cancelled ? "Aborted" : "Error", ended.EndReason);
        var requestEnd = Assert.Single(events, entry => entry.Kind == "RequestEnd");
        Assert.NotNull(requestEnd.FirstByteMs);
        Assert.True(requestEnd.CleanupMs >= 0);
        if (cancelled)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
            Assert.NotNull(requestEnd.CancelledAtMs);
            Assert.True(requestEnd.CancellationToCompletionMs >= 0);
        }
        else
        {
            Assert.IsType<IOException>(exception);
            Assert.Null(requestEnd.CancelledAtMs);
        }
    }

    private static DefaultHttpContext NewGetContext(string range, Stream body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = "/content/movie.mkv";
        context.Request.Headers.Range = range;
        context.Response.Body = body;
        return context;
    }

    private sealed class MemoryStoreItem(int chunkSize = 1024) : BaseStoreReadonlyItem
    {
        public override string Name => "movie.mkv";
        public override string UniqueKey => "movie";
        public override long FileSize => 1024;
        public override DateTime CreatedAt => DateTime.UnixEpoch;
        public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new ChunkedReadStream(chunkSize));
    }

    private sealed class ChunkedReadStream(int chunkSize) : MemoryStream(new byte[1024])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }

    private sealed class InterruptedWriteStream(bool cancelled, CancellationTokenSource cancellation) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Length > 0)
            {
                if (cancelled)
                {
                    await cancellation.CancelAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                throw new IOException("Test response write failure.");
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class GatedWriteStream(bool gated) : MemoryStream
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            if (gated)
                await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

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
