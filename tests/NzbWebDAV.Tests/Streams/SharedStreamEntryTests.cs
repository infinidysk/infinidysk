using System.Text;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(GlobalLoggerCollection))]
public class SharedStreamEntryTests
{
    [Fact]
    public async Task TwoReaders_AreByteExact_AndFetchEachSegmentOnce()
    {
        var (upstream, client, payload) = CreateUpstream();
        await using var entry = StartEntry(upstream, payload.Length);
        await using var first = Attach(entry, 0);
        await using var second = Attach(entry, 0);

        var a = await ReadAllAsync(first);
        var b = await ReadAllAsync(second);

        Assert.Equal(payload, a);
        Assert.Equal(payload, b);
        Assert.NotEmpty(client.BodyRequestCounts);
        Assert.All(client.BodyRequestCounts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task MidStreamAttach_ServesFromRingWithoutRefetch()
    {
        var (upstream, client, payload) = CreateUpstream(segmentCount: 4, segmentSize: 16);
        await using var entry = StartEntry(upstream, payload.Length, ringSize: 64, chunkSize: 8, leadBytes: 32);
        await using var leader = Attach(entry, 0);
        var prefix = new byte[24];
        Assert.Equal(24, await ReadExactAsync(leader, prefix));
        await WaitUntil(() => entry.Ring.Frontier >= 24);
        var attachAt = entry.Ring.TailStart;
        Assert.True(attachAt <= 24);

        var countsAfterLeader = SnapshotCounts(client);
        await using var follower = Attach(entry, attachAt);
        var fromRing = new byte[Math.Max(1, 24 - (int)attachAt)];
        Assert.Equal(fromRing.Length, await ReadExactAsync(follower, fromRing));

        Assert.Equal(payload.AsSpan((int)attachAt, fromRing.Length).ToArray(), fromRing);
        Assert.Equal(countsAfterLeader, SnapshotCounts(client));
    }

    [Fact]
    public async Task DisconnectingOneReader_LeavesTheOtherReadingToEof()
    {
        var (upstream, _, payload) = CreateUpstream();
        await using var entry = StartEntry(upstream, payload.Length);
        var first = Attach(entry, 0);
        await using var second = Attach(entry, 0);
        Assert.Equal(1, await first.ReadAsync(new byte[1]));
        await first.DisposeAsync();

        var rest = await ReadAllAsync(second);
        Assert.Equal(payload, rest);
    }

    [Fact]
    public async Task LastReaderGrace_DisposesUpstreamAndReleasesRing()
    {
        var budget = new InFlightArticleBudget(64 * 1024 * 1024);
        var (upstream, _, payload) = CreateUpstream(budget: budget);
        var clock = new ControllableTimeProvider();
        var entry = StartEntry(
            upstream, payload.Length, clock: clock, grace: TimeSpan.FromSeconds(2));
        var reader = Attach(entry, 0);
        Assert.Equal(payload, await ReadAllAsync(reader));
        await reader.DisposeAsync();

        Assert.Equal(SharedStreamEntryState.Draining, entry.State);
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => entry.State == SharedStreamEntryState.Disposed);
        Assert.Equal(0, entry.Ring.RetainedBytes);
        Assert.True(entry.Ring.IsReleased);
        Assert.Equal(0, budget.LeasedBytes);
        Assert.Equal(SharedStreamReapReason.Grace, entry.ReapReason);
    }

    [Fact]
    public async Task AttachDuringGrace_CancelsTeardown()
    {
        var (upstream, _, payload) = CreateUpstream();
        var clock = new ControllableTimeProvider();
        await using var entry = StartEntry(
            upstream, payload.Length, clock: clock, grace: TimeSpan.FromSeconds(5));
        var first = Attach(entry, 0);
        var prefix = new byte[24];
        Assert.Equal(24, await ReadExactAsync(first, prefix));
        var reattachAt = first.Cursor;
        await first.DisposeAsync();
        Assert.Equal(SharedStreamEntryState.Draining, entry.State);

        await using var second = Attach(entry, reattachAt);
        Assert.Equal(SharedStreamEntryState.Ready, entry.State);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(SharedStreamEntryState.Ready, entry.State);
        Assert.Equal(payload[(int)reattachAt..], await ReadAllAsync(second));
    }

    [Fact]
    public async Task GraceExpiry_WinsOverLateAttach()
    {
        var (upstream, _, payload) = CreateUpstream();
        var clock = new ControllableTimeProvider();
        var entry = StartEntry(
            upstream, payload.Length, clock: clock, grace: TimeSpan.FromSeconds(1));
        var reader = Attach(entry, 0);
        await ReadAllAsync(reader);
        await reader.DisposeAsync();

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => entry.State >= SharedStreamEntryState.Disposing);
        var missed = entry.TryAttach(0, NoFallback, out var reason);
        Assert.Null(missed);
        Assert.Equal(SharedStreamAttachMissReason.EntryUnusable, reason);
        await WaitUntil(() => entry.State == SharedStreamEntryState.Disposed);
    }

    [Fact]
    public async Task AttachRacingGraceExpiry_HasExactlyOneWinner()
    {
        var (upstream, _, payload) = CreateUpstream();
        var clock = new ControllableTimeProvider();
        var entry = StartEntry(
            upstream, payload.Length, clock: clock, grace: TimeSpan.FromMilliseconds(1));
        var reader = Attach(entry, 0);
        await ReadAllAsync(reader);
        await reader.DisposeAsync();
        Assert.Equal(SharedStreamEntryState.Draining, entry.State);

        SharedReaderStream? attached = null;
        var attach = Task.Run(() => attached = entry.TryAttach(0, NoFallback, out _));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await attach;

        if (attached is not null)
        {
            Assert.True(entry.State is SharedStreamEntryState.Ready or SharedStreamEntryState.Draining);
            Assert.True(entry.State < SharedStreamEntryState.Disposing);
            await attached.DisposeAsync();
            await entry.DisposeAsync();
        }
        else
        {
            await WaitUntil(() => entry.State == SharedStreamEntryState.Disposed);
            Assert.Null(entry.TryAttach(0, NoFallback, out _));
        }
    }

    [Fact]
    public async Task PumpFailure_ThrowsOncePerReader_AndRejectsLaterAttach()
    {
        var boom = new IOException("upstream failed");
        var upstream = new ThrowingReadStream(new byte[64], throwAfter: 8, boom);
        await using var entry = StartEntry(upstream, 64, leadBytes: 4, chunkSize: 8, ringSize: 32);
        await using var first = Attach(entry, 0);
        await using var second = Attach(entry, 0);

        var a = Assert.ThrowsAsync<IOException>(async () => await ReadAllAsync(first));
        var b = Assert.ThrowsAsync<IOException>(async () => await ReadAllAsync(second));
        Assert.Same(boom, await a);
        Assert.Same(boom, await b);
        await WaitUntil(() => entry.State >= SharedStreamEntryState.Disposing);
        Assert.Null(entry.TryAttach(0, NoFallback, out var reason));
        Assert.Equal(SharedStreamAttachMissReason.EntryUnusable, reason);
        Assert.Equal(SharedStreamReapReason.Failure, entry.ReapReason);
    }

    [Fact]
    public async Task PumpFailure_WakesParkedWaiterExactlyOnce()
    {
        var boom = new IOException("boom");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new GatedThrowingStream(gate, boom);
        await using var entry = StartEntry(upstream, 32, leadBytes: 4, chunkSize: 8, ringSize: 16);
        await using var reader = Attach(entry, 0);
        var parked = reader.ReadAsync(new byte[8], CancellationToken.None).AsTask();
        await WaitUntil(() => !parked.IsCompleted);
        gate.SetResult();
        var ex = await Assert.ThrowsAsync<IOException>(() => parked);
        Assert.Same(boom, ex);
    }

    [Fact]
    public async Task ReaderCancelWhileParked_UnwindsOnlyThatReader()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz123456");
        var upstream = new GatedDataStream(gate, payload);
        await using var entry = StartEntry(upstream, payload.Length, leadBytes: 8, chunkSize: 8, ringSize: 32);
        await using var survivor = Attach(entry, 0);
        await using var parked = Attach(entry, 0);
        using var cts = new CancellationTokenSource();
        var waiting = parked.ReadAsync(new byte[8], cts.Token).AsTask();
        await WaitUntil(() => !waiting.IsCompleted);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await parked.DisposeAsync();

        gate.TrySetResult();
        Assert.Equal(payload, await ReadAllAsync(survivor));
    }

    [Fact]
    public async Task SlowReaderForceEviction_ResumesByteExactOnPrivateFallback()
    {
        var (upstream, client, payload) = CreateUpstream(segmentCount: 8, segmentSize: 16);
        await using var entry = StartEntry(
            upstream, payload.Length, ringSize: 16, chunkSize: 8, leadBytes: 8);
        await using var slow = Attach(entry, 0, (offset, _) =>
        {
            var (privateStream, _, _) = CreateUpstream(segmentCount: 8, segmentSize: 16);
            privateStream.Seek(offset, SeekOrigin.Begin);
            return Task.FromResult<Stream>(privateStream);
        });
        await using var fast = Attach(entry, 0);

        var head = new byte[4];
        Assert.Equal(4, await ReadExactAsync(slow, head));
        Assert.Equal(payload.AsSpan(0, 4).ToArray(), head);

        var fastBytes = await ReadAllAsync(fast);
        Assert.Equal(payload, fastBytes);
        await WaitUntil(() => slow.IsDetached || entry.Ring.TailStart > 4, TimeSpan.FromSeconds(5));

        var remainder = await ReadAllAsync(slow);
        Assert.Equal(payload[4..], remainder);
    }

    [Fact]
    public async Task PausedSoleReader_PlateausBodyRequestsAtLeadBytes()
    {
        var (upstream, client, payload) = CreateUpstream(segmentCount: 8, segmentSize: 16);
        await using var entry = StartEntry(
            upstream, payload.Length, ringSize: 64, chunkSize: 8, leadBytes: 16);
        await using var reader = Attach(entry, 0);
        var prefix = new byte[8];
        Assert.Equal(8, await ReadExactAsync(reader, prefix));

        var plateau = await WaitForPlateau(() => client.BodyRequestCount);
        await Task.Delay(150);
        Assert.Equal(plateau, client.BodyRequestCount);
        Assert.True(entry.Ring.Frontier - 8 >= 16 || entry.Ring.IsComplete);
    }

    [Fact]
    public async Task InWindowSeek_DoesNotIssueNewBodies()
    {
        var (upstream, client, payload) = CreateUpstream(segmentCount: 4, segmentSize: 16);
        await using var entry = StartEntry(
            upstream, payload.Length, ringSize: 64, chunkSize: 8, leadBytes: 32);
        await using var pinner = Attach(entry, 0);
        await using var reader = Attach(entry, 0);
        var prefix = new byte[24];
        Assert.Equal(24, await ReadExactAsync(reader, prefix));
        var counts = SnapshotCounts(client);

        reader.Seek(8, SeekOrigin.Begin);
        var replay = new byte[8];
        Assert.Equal(8, await ReadExactAsync(reader, replay));
        Assert.Equal(payload.AsSpan(8, 8).ToArray(), replay);
        Assert.Equal(counts, SnapshotCounts(client));
    }

    [Fact]
    public async Task OutOfWindowSeek_DetachesToPrivateFallback()
    {
        var (upstream, _, payload) = CreateUpstream(segmentCount: 4, segmentSize: 16);
        await using var entry = StartEntry(
            upstream, payload.Length, ringSize: 8, chunkSize: 8, leadBytes: 8);
        var fallbackAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reader = Attach(entry, 0, (offset, _) =>
        {
            fallbackAt.TrySetResult(offset);
            var stream = TestStreams.Create(payload);
            stream.Seek(offset, SeekOrigin.Begin);
            return Task.FromResult(stream);
        });
        Assert.Equal(8, await ReadExactAsync(reader, new byte[8]));
        reader.Seek(payload.Length - 4, SeekOrigin.Begin);
        var tail = new byte[4];
        Assert.Equal(4, await ReadExactAsync(reader, tail));
        Assert.Equal(payload[^4..], tail);
        Assert.True(fallbackAt.Task.IsCompleted);
        Assert.Equal(payload.Length - 4, await fallbackAt.Task);
        Assert.True(reader.IsDetached);
    }

    [Fact]
    public async Task Eof_ReturnsZero()
    {
        var (upstream, _, payload) = CreateUpstream();
        await using var entry = StartEntry(upstream, payload.Length);
        await using var reader = Attach(entry, 0);
        Assert.Equal(payload, await ReadAllAsync(reader));
        Assert.Equal(0, await reader.ReadAsync(new byte[8]));
        Assert.Equal(0, await reader.ReadAsync(new byte[8]));
    }

    [Fact]
    public void OpeningEntry_IsNotAttachable()
    {
        var entry = new SharedStreamEntry(
            "/content/movie.mkv", 0, 64, 32, TimeSpan.FromSeconds(1), CancellationToken.None,
            chunkSize: 8, leadBytes: 8);
        Assert.Equal(SharedStreamEntryState.Opening, entry.State);
        Assert.False(entry.IsAttachable);
        Assert.Null(entry.TryAttach(0, NoFallback, out var reason));
        Assert.Equal(SharedStreamAttachMissReason.EntryUnusable, reason);
        entry.AbandonOpening();
    }

    private static SharedReaderStream Attach(
        SharedStreamEntry entry,
        long start,
        SharedStreamFallbackFactory? fallback = null)
    {
        var reader = entry.TryAttach(start, fallback ?? NoFallback, out var reason);
        Assert.True(reader is not null, $"attach missed: {reason}");
        return reader!;
    }

    private static SharedStreamEntry StartEntry(
        Stream upstream,
        long fileSize,
        long ringSize = 64,
        TimeSpan? grace = null,
        TimeProvider? clock = null,
        int chunkSize = 8,
        int leadBytes = 16,
        long anchor = 0)
    {
        var entry = new SharedStreamEntry(
            "/content/movie.mkv",
            anchor,
            fileSize,
            ringSize,
            grace ?? TimeSpan.FromSeconds(10),
            CancellationToken.None,
            clock,
            chunkSize,
            leadBytes);
        entry.BindAndStart(new DetachedStreamLease
        {
            Stream = upstream,
            Ownership = NullAsyncDisposable.Instance,
        });
        return entry;
    }

    private static (NzbFileStream Stream, FakeNntpClient Client, byte[] Payload) CreateUpstream(
        int segmentCount = 4,
        int segmentSize = 16,
        InFlightArticleBudget? budget = null)
    {
        var payload = new byte[segmentCount * segmentSize];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)('a' + (i % 26));

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
        return (stream, client, payload);
    }

    private static Task<Stream> NoFallback(long offset, CancellationToken _) =>
        throw new InvalidOperationException($"Private fallback should not run at offset {offset}.");

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] dest)
    {
        var read = 0;
        while (read < dest.Length)
        {
            var n = await stream.ReadAsync(dest.AsMemory(read));
            if (n == 0) break;
            read += n;
        }

        return read;
    }

    private static Dictionary<string, int> SnapshotCounts(FakeNntpClient client) =>
        client.BodyRequestCounts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

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

    private static async Task<int> WaitForPlateau(Func<int> sample)
    {
        var last = -1;
        var stable = 0;
        for (var i = 0; i < 80; i++)
        {
            var value = sample();
            if (value == last) stable++;
            else
            {
                last = value;
                stable = 0;
            }

            if (stable >= 5) return value;
            await Task.Delay(20);
        }

        return last;
    }

    private sealed class ThrowingReadStream(byte[] data, int throwAfter, Exception error) : MemoryStream(data)
    {
        private int _consumed;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_consumed >= throwAfter)
                throw error;
            var allowed = Math.Min(buffer.Length, throwAfter - _consumed);
            var read = await base.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
            _consumed += read;
            return read;
        }
    }

    private sealed class GatedThrowingStream(
        TaskCompletionSource gate,
        Exception error) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 32;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => Position
        };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw error;
        }
    }

    private sealed class GatedDataStream(TaskCompletionSource gate, byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => _position;
            set => _position = (int)value;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = (int)(origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => data.Length + offset,
                _ => _position
            });
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_position >= data.Length) return 0;
            var n = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return n;
        }
    }
}
