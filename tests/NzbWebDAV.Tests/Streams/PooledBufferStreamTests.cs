using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(GlobalLoggerCollection))]
public class PooledBufferStreamTests
{
    [Fact]
    public async Task CopyToAsync_RoundTripsBytes()
    {
        var sourceBytes = Enumerable.Range(0, 1000).Select(i => (byte)(i % 256)).ToArray();
        await using var source = new MemoryStream(sourceBytes);
        await using var buffer = new PooledBufferStream(capacityHint: 64);
        await source.CopyToAsync(buffer);
        buffer.Position = 0;

        using var output = new MemoryStream();
        await buffer.CopyToAsync(output);
        Assert.Equal(sourceBytes, output.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(100)]
    public void Write_GrowsPastCapacityHint_PreservesData(int capacityHint)
    {
        var payload = Enumerable.Range(0, 100).Select(i => (byte)(i + 1)).ToArray();
        using var stream = new PooledBufferStream(capacityHint);
        stream.Write(payload);
        stream.Position = 0;

        var read = new byte[payload.Length];
        Assert.Equal(payload.Length, stream.Read(read));
        Assert.Equal(payload, read);
        Assert.Equal(payload.Length, stream.Length);
    }

    [Fact]
    public void SetLength_Growth_ExposesZerosIncludingTruncateThenGrow()
    {
        using var stream = new PooledBufferStream(16);
        stream.Write(Enumerable.Repeat((byte)0xAB, 16).ToArray());
        stream.SetLength(8);
        Assert.Equal(8, stream.Length);

        // Dirty the region beyond Length by writing into a rented-looking pattern:
        // grow again and require zeros in the newly exposed region.
        stream.SetLength(16);
        stream.Position = 8;
        var tail = new byte[8];
        Assert.Equal(8, stream.Read(tail));
        Assert.Equal(new byte[8], tail);
    }

    [Fact]
    public void SetLength_Growth_ClearsDirtyRentedRegion()
    {
        using var stream = new PooledBufferStream(32);
        // Force a rent, then truncate without clearing the physical array past Length.
        stream.Write(Enumerable.Repeat((byte)0xFF, 32).ToArray());
        stream.SetLength(0);
        stream.SetLength(32);
        stream.Position = 0;
        var bytes = new byte[32];
        Assert.Equal(32, stream.Read(bytes));
        Assert.Equal(new byte[32], bytes);
    }

    [Fact]
    public async Task SyncAndAsyncWrites_LandSameBytes()
    {
        var payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();

        using var sync = new PooledBufferStream(0);
        sync.Write(payload, 0, payload.Length);

        await using var asyncMemory = new PooledBufferStream(0);
        await asyncMemory.WriteAsync(payload.AsMemory());

        await using var asyncArray = new PooledBufferStream(0);
        await asyncArray.WriteAsync(payload, 0, payload.Length);

        sync.Position = 0;
        asyncMemory.Position = 0;
        asyncArray.Position = 0;
        var syncBytes = new byte[payload.Length];
        var asyncMemoryBytes = new byte[payload.Length];
        var asyncArrayBytes = new byte[payload.Length];
        Assert.Equal(payload.Length, sync.Read(syncBytes));
        Assert.Equal(payload.Length, asyncMemory.Read(asyncMemoryBytes));
        Assert.Equal(payload.Length, asyncArray.Read(asyncArrayBytes));
        Assert.Equal(syncBytes, asyncMemoryBytes);
        Assert.Equal(syncBytes, asyncArrayBytes);
        Assert.Equal(payload, syncBytes);
    }

    [Fact]
    public void Write_AfterSeekingPastEnd_ZeroesTheGap()
    {
        using var stream = new PooledBufferStream(16);
        stream.Write(Enumerable.Repeat((byte)0xFF, 16).ToArray());
        stream.SetLength(0);
        stream.Position = 8;
        stream.WriteByte(1);
        stream.Position = 0;

        var bytes = new byte[9];
        Assert.Equal(9, stream.Read(bytes));
        Assert.Equal(new byte[8], bytes[..8]);
        Assert.Equal(1, bytes[8]);
    }

    [Fact]
    public void SpanWrite_LandsSameBytes()
    {
        var payload = Enumerable.Range(0, 20).Select(i => (byte)(255 - i)).ToArray();
        using var stream = new PooledBufferStream(4);
        stream.Write(payload.AsSpan());
        stream.Position = 0;
        var read = new byte[payload.Length];
        Assert.Equal(payload.Length, stream.Read(read.AsSpan()));
        Assert.Equal(payload, read);
    }

    [Fact]
    public void DoubleDispose_IsSafe_AndUseAfterDisposeThrows()
    {
        var stream = new PooledBufferStream(8);
        stream.WriteByte(1);
        stream.Dispose();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(2));
        Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
    }

    [Fact]
    public void Constructor_CapacityPast32Mb_LogsRunawayWarning()
    {
        var events = CaptureLogs(() =>
        {
            using var stream = new PooledBufferStream((32 * 1024 * 1024) + 1);
        });

        Assert.Contains(events, entry =>
            entry.Level == LogEventLevel.Warning &&
            entry.MessageTemplate.Text.Contains("Segment buffer allocation or growth exceeded", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_CapacityUnder32Mb_DoesNotLogRunawayWarning()
    {
        var events = CaptureLogs(() =>
        {
            using var stream = new PooledBufferStream(1024);
        });

        Assert.DoesNotContain(events, entry =>
            entry.MessageTemplate.Text.Contains("Segment buffer allocation or growth exceeded", StringComparison.Ordinal));
    }

    private static IReadOnlyList<LogEvent> CaptureLogs(Action action)
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            action();
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

public class ZeroStreamTests
{
    [Fact]
    public void Read_EmitsExactZerosAcrossOddSizedReads()
    {
        using var stream = new ZeroStream(17);
        Assert.Equal(17, stream.Length);
        Assert.Equal(0, stream.Position);

        var first = new byte[5];
        Assert.Equal(5, stream.Read(first));
        Assert.Equal(new byte[5], first);
        Assert.Equal(5, stream.Position);

        var second = new byte[10];
        Assert.Equal(10, stream.Read(second));
        Assert.Equal(new byte[10], second);

        var third = new byte[8];
        Assert.Equal(2, stream.Read(third));
        Assert.Equal(new byte[2], third[..2]);
        Assert.Equal(17, stream.Position);
        Assert.Equal(0, stream.Read(third));
    }

    [Fact]
    public void Seek_TracksPosition()
    {
        using var stream = new ZeroStream(100);
        Assert.Equal(50, stream.Seek(50, SeekOrigin.Begin));
        Assert.Equal(50, stream.Position);
        Assert.Equal(75, stream.Seek(25, SeekOrigin.Current));
        Assert.Equal(90, stream.Seek(-10, SeekOrigin.End));
    }
}
