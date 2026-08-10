using System.Text;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(GlobalLoggerCollection))]
public class StreamHelperTests
{
    [Fact]
    public async Task CachedYencStream_ReturnsHeadersAndDecodedBytes()
    {
        var header = Header(partSize: 4);
        await using var stream = new CachedYencStream(
            header,
            new MemoryStream([1, 2, 3, 4], writable: false));
        var buffer = new byte[4];

        var returnedHeader = await stream.GetYencHeadersAsync();
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: true);

        Assert.Same(header, returnedHeader);
        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer);
    }

    [Fact]
    public void CachedYencStream_DisposesDecodedStream()
    {
        var inner = new TrackingMemoryStream([1]);
        var stream = new CachedYencStream(Header(partSize: 1), inner);

        stream.Dispose();

        Assert.True(inner.Disposed);
    }

    [Fact]
    public void DisposableCallbackStream_SynchronousDisposeRunsOnce()
    {
        var inner = new TrackingMemoryStream([1]);
        var callbacks = 0;
        var stream = new DisposableCallbackStream(
            inner,
            onDispose: () => callbacks++);

        stream.Dispose();
        stream.Dispose();

        Assert.True(inner.Disposed);
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public async Task DisposableCallbackStream_AsyncDisposePrefersAsyncCallbackAndRunsOnce()
    {
        var syncCallbacks = 0;
        var asyncCallbacks = 0;
        var stream = new DisposableCallbackStream(
            new MemoryStream(),
            onDispose: () => syncCallbacks++,
            onDisposeAsync: () =>
            {
                asyncCallbacks++;
                return ValueTask.CompletedTask;
            });

        await stream.DisposeAsync();
        await stream.DisposeAsync();

        Assert.Equal(0, syncCallbacks);
        Assert.Equal(1, asyncCallbacks);
    }

    [Fact]
    public async Task CountingYencStream_CountsPositiveReadsButNotEndOfStream()
    {
        var innerBytes = new TrackingMemoryStream([1, 2, 3]);
        var inner = new CachedYencStream(Header(partSize: 3), innerBytes);
        var tracker = new ProviderBytesTracker();
        await using var stream = new CountingYencStream(inner, tracker, "provider");
        var buffer = new byte[8];

        Assert.Same(await inner.GetYencHeadersAsync(), await stream.GetYencHeadersAsync());
        Assert.Equal(3, await stream.ReadAsync(buffer));
        Assert.Equal(0, await stream.ReadAsync(buffer));

        Assert.Equal(3, tracker.GetLifetime("provider"));
        Assert.Equal(3, tracker.LifetimeAll);
    }

    [Fact]
    public async Task CountingYencStream_DisposedTwice_RecordsOneThroughputSample()
    {
        var tracker = new ProviderBytesTracker();
        var stream = new CountingYencStream(
            new CachedYencStream(Header(partSize: 3), new TrackingMemoryStream([1, 2, 3])),
            tracker,
            "provider");

        Assert.Equal(3, await stream.ReadAsync(new byte[8]));
        stream.Dispose();
        Assert.True(tracker.GetBytesPerMs("provider") > 0, "the first dispose should record a sample");

        // Another body for the same provider moves the average. Without it the replay is
        // harmless, since re-applying a sample the average already sits on is a no-op.
        tracker.RecordSegmentThroughput("provider", 1_000_000, 1);
        var afterSecondSample = tracker.GetBytesPerMs("provider");

        // Replaying the stale sample now would drag provider selection backwards.
        stream.Dispose();

        Assert.Equal(afterSecondSample, tracker.GetBytesPerMs("provider"));
    }

    [Fact]
    public void CountingYencStream_DisposesInnerStream()
    {
        var innerBytes = new TrackingMemoryStream([1]);
        var stream = new CountingYencStream(
            new CachedYencStream(Header(partSize: 1), innerBytes),
            new ProviderBytesTracker(),
            "provider");

        stream.Dispose();

        Assert.True(innerBytes.Disposed);
    }

    [Fact]
    public async Task ZeroFillLogLimiter_CoalescesRepeatedWarningsPerFile()
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var fileName = $"coalesced-{Guid.NewGuid():N}.bin";
        var otherFileName = $"independent-{Guid.NewGuid():N}.bin";

        try
        {
            ZeroFillLogLimiter.Write(
                "Article {SegmentId} missing from {FileName}; filling {Bytes} bytes.",
                "one", fileName, 100);
            ZeroFillLogLimiter.Write(
                "Article {SegmentId} missing from {FileName}; filling {Bytes} bytes.",
                "two", fileName, 100);
            ZeroFillLogLimiter.Write(
                "Article {SegmentId} missing from {FileName}; filling {Bytes} bytes.",
                "three", otherFileName, 100);

            await Task.Yield();

            Assert.Single(sink.Events, logEvent =>
                logEvent.RenderMessage().Contains(fileName, StringComparison.Ordinal));
            Assert.Single(sink.Events, logEvent =>
                logEvent.RenderMessage().Contains(otherFileName, StringComparison.Ordinal));
        }
        finally
        {
            Log.Logger = previous;
        }
    }


    [Fact]
    public async Task ZeroFillLogLimiter_CoalescesUnicodeFilenameVariants()
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var baseName = $"unicode-{Guid.NewGuid():N}";
        var fileNameNfc = baseName + "\u00E9.bin";
        var fileNameNfd = baseName + "e\u0301.bin";

        try
        {
            ZeroFillLogLimiter.Write(
                "Article {SegmentId} missing from {FileName}; filling {Bytes} bytes.",
                "one", fileNameNfc, 100);
            ZeroFillLogLimiter.Write(
                "Article {SegmentId} missing from {FileName}; filling {Bytes} bytes.",
                "two", fileNameNfd, 100);

            await Task.Yield();

            Assert.Single(sink.Events, logEvent =>
                logEvent.RenderMessage().Contains(baseName, StringComparison.Ordinal));
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task ThrottledSegmentWarning_CoalescesUnicodeFilenameVariants()
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var baseName = $"unicode-{Guid.NewGuid():N}";
        var keyNfc = $"provider|segment|{baseName}\u00E9.bin";
        var keyNfd = $"provider|segment|{baseName}e\u0301.bin";

        try
        {
            ThrottledSegmentWarning.Write(keyNfc, "Segment warning for {Key}", keyNfc);
            ThrottledSegmentWarning.Write(keyNfd, "Segment warning for {Key}", keyNfd);
            await Task.Yield();
            Assert.Single(sink.Events, logEvent =>
                logEvent.RenderMessage().Contains("Segment warning", StringComparison.Ordinal));
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task MultipartFileStream_ReadsAndSeeksAcrossParts()
    {
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = [1, 2, 3],
                ["two"] = [4, 5, 6, 7],
            },
            useCachedYencStreams: true);
        var multipart = new MultipartFile
        {
            FileParts =
            [
                new MultipartFile.FilePart
                {
                    NzbFile = File("one", size: 3),
                    ByteRange = new LongRange(0, 3),
                },
                new MultipartFile.FilePart
                {
                    NzbFile = File("two", size: 4),
                    ByteRange = new LongRange(3, 7),
                },
            ],
        };

        await using (var stream = new MultipartFileStream(multipart, client))
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, output.ToArray());
        }

        await using (var stream = new MultipartFileStream(multipart, client))
        {
            Assert.Equal(4, stream.Seek(4, SeekOrigin.Begin));
            var buffer = new byte[3];
            Assert.Equal(3, await stream.ReadAtLeastAsync(
                buffer, buffer.Length, throwOnEndOfStream: true));
            Assert.Equal(new byte[] { 5, 6, 7 }, buffer);
            Assert.Equal(7, stream.Position);
        }
    }

    private static UsenetYencHeader Header(long partSize) =>
        new()
        {
            FileName = "cached.bin",
            FileSize = partSize,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartOffset = 0,
            PartSize = partSize,
        };

    private static NzbFile File(string segmentId, long size)
    {
        var file = new NzbFile { Subject = $"{segmentId}.bin" };
        file.Segments.Add(new NzbSegment
        {
            Bytes = size,
            MessageId = segmentId,
            Number = 1,
            ByteRange = new LongRange(0, size),
        });
        return file;
    }

    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
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
