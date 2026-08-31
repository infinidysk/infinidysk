using System.Text;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.Tests.Streams;

public class NzbFileStreamFirstByteTests
{
    private static readonly byte[][] SegmentBytes =
    [
        Encoding.ASCII.GetBytes("abcde"),
        Encoding.ASCII.GetBytes("fghij"),
        Encoding.ASCII.GetBytes("klmno")
    ];

    private static readonly string[] SegmentIds = ["one", "two", "three"];
    private static readonly LongRange[] SegmentRanges =
    [
        new(0, 5),
        new(5, 10),
        new(10, 15)
    ];

    private const long LargeBudget = 2L * 1024 * 1024;

    [Fact]
    public async Task ExactIndexedSeek_ReturnsFirstRequestedByteBeforeTargetBodyEof()
    {
        var staged = new StagedBodyStream("f"u8.ToArray(), "g"u8.ToArray(), "hij"u8.ToArray());
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "two" ? staged : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client);
        stream.Seek(6, SeekOrigin.Begin);
        Assert.Equal(0, client.BatchRequestCount);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'g', buffer[0]);
        Assert.True(staged.TailGateClosed);
        Assert.False(staged.TailReadStarted.IsCompleted);
        Assert.Equal(0, client.HeaderProbeCount);
        Assert.Equal(1, client.BodyRequestCounts["two"]);
    }

    [Fact]
    public async Task ExactIndexedSeek_DoesNotProbeHeaders()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client);
        stream.Seek(7, SeekOrigin.Begin);

        var buffer = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(buffer));
        Assert.Equal("hij", Encoding.ASCII.GetString(buffer));
        Assert.Equal(0, client.HeaderProbeCount);
    }

    [Fact]
    public async Task ExactIndexedSeek_DoesNotDrainUnrequestedTargetSuffixBeforeFirstRead()
    {
        var staged = new StagedBodyStream("fg"u8.ToArray(), "h"u8.ToArray(), "ij"u8.ToArray());
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "two" ? staged : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client);
        stream.Seek(7, SeekOrigin.Begin);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.True(staged.TailGateClosed);
        Assert.Equal(3, staged.TotalBytesRead);
    }

    [Fact]
    public async Task ExactIndexedSeek_LargeRangeBypassesLegacyBufferedSeekHead()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(6, SeekOrigin.Begin);

        var buffer = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(buffer));
        Assert.Equal("gh", Encoding.ASCII.GetString(buffer));
        Assert.Equal(0, client.HeaderProbeCount);
        Assert.Equal(1, client.BodyRequestCounts.GetValueOrDefault("two"));
    }

    [Fact]
    public async Task LegacySeek_LargeRangeRetainsBufferedFastPath()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, articleBufferSize: 4, segmentByteRanges: null);
        stream.Seek(6, SeekOrigin.Begin);

        var buffer = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(buffer));
        Assert.Equal("gh", Encoding.ASCII.GetString(buffer));
        Assert.True(client.BodyRequestCounts["two"] >= 1);
    }

    [Fact]
    public async Task LegacySeek_SmallRangeRetainsHeaderProbedUnbufferedPath()
    {
        var client = CreateClient();
        using var _ = SetBudget(2);
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, articleBufferSize: 4, segmentByteRanges: null);
        stream.Seek(6, SeekOrigin.Begin);

        var buffer = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(buffer));
        Assert.Equal("gh", Encoding.ASCII.GetString(buffer));
        Assert.True(client.HeaderProbeCount >= 1);
        Assert.Equal(0, client.BatchRequestCount);
    }

    [Fact]
    public async Task ExactIndexedSeek_LastSegmentReturnsFirstByteBeforeTargetBodyEof()
    {
        var staged = new StagedBodyStream("kl"u8.ToArray(), "m"u8.ToArray(), "no"u8.ToArray());
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "three" ? staged : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(12, SeekOrigin.Begin);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'m', buffer[0]);
        Assert.True(staged.TailGateClosed);
        Assert.False(client.BodyRequestCounts.ContainsKey("one"));
        Assert.False(client.BodyRequestCounts.ContainsKey("two"));
        Assert.Equal(1, client.BodyRequestCounts["three"]);
        Assert.Equal(0, client.BatchRequestCount);
    }

    [Fact]
    public async Task ExactIndexedSeek_NearArticleEndUsesOnlyVisibleTailForBudget()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(9, SeekOrigin.Begin);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.BatchRequestCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(client.BatchRequestCount > 0);
    }

    [Fact]
    public async Task ExactIndexedSeek_RangeWithinTargetNeverStartsRemainder()
    {
        var client = CreateClient();
        using var _ = SetBudget(2);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(6, SeekOrigin.Begin);

        var buffer = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(buffer));
        Assert.Equal("gh", Encoding.ASCII.GetString(buffer));
        await Task.Delay(50);
        Assert.Equal(0, client.BatchRequestCount);
        Assert.Equal(1, client.BodyRequestCounts["two"]);
    }

    [Fact]
    public async Task ExactIndexedSeek_CrossingBoundaryReturnsExactBytes()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(8, SeekOrigin.Begin);

        var buffer = new byte[4];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0) break;
            read += n;
        }

        Assert.Equal(4, read);
        Assert.Equal("ijkl", Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public async Task ExactIndexedSeek_StartingAtBoundaryDoesNotOpenPreviousArticle()
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(5, SeekOrigin.Begin);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'f', buffer[0]);
        Assert.False(client.BodyRequestCounts.ContainsKey("one"));
        Assert.Equal(1, client.BodyRequestCounts["two"]);
    }

    [Fact]
    public async Task ExactIndexedSeek_OneArticleFileNeverCreatesRemainder()
    {
        var bytes = Encoding.ASCII.GetBytes("abcdefghij");
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]> { ["only"] = bytes },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange> { ["only"] = new(0, 10) });
        using var _ = SetBudget(LargeBudget);
        await using var stream = new NzbFileStream(
            ["only"], 10, client, articleBufferSize: 4, segmentByteRanges: [new LongRange(0, 10)]);
        stream.Seek(3, SeekOrigin.Begin);

        var buffer = new byte[4];
        Assert.Equal(4, await stream.ReadAsync(buffer));
        Assert.Equal("defg", Encoding.ASCII.GetString(buffer));
        Assert.Equal(0, client.BatchRequestCount);
        Assert.Equal(1, client.BodyRequestCounts["only"]);
    }

    [Fact]
    public async Task ExactIndexedSeek_PrefixDiscardEofThrowsAndDisposesBody()
    {
        var opened = new List<ImmediateEofStream>();
        var client = new FakeNntpClient(
            SegmentIds.Zip(SegmentBytes).ToDictionary(pair => pair.First, pair => pair.Second),
            useCachedYencStreams: true,
            SegmentIds.Zip(SegmentRanges).ToDictionary(pair => pair.First, pair => pair.Second),
            (id, bytes) =>
            {
                if (id != "two")
                    return new MemoryStream(bytes, writable: false);
                var body = new ImmediateEofStream();
                opened.Add(body);
                return body;
            });
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client);
        stream.Seek(6, SeekOrigin.Begin);

        await Assert.ThrowsAsync<SeekPositionNotFoundException>(
            async () => await stream.ReadAsync(new byte[1]));
        Assert.All(opened, body => Assert.True(body.Disposed));
        Assert.Equal(0, client.BatchRequestCount);
    }

    [Fact]
    public async Task ExactIndexedSeek_MismatchedResponseIdDisposesAndThrows()
    {
        var client = CreateClient();
        client.ForcedResponseSegmentId = "other";
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client);
        stream.Seek(6, SeekOrigin.Begin);

        await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(
            async () => await stream.ReadAsync(new byte[1]));
        Assert.Equal(0, client.BatchRequestCount);
    }

    [Fact]
    public async Task ExactIndexedSeek_KnownMissingUsesLocalBodyBeforeGapFill()
    {
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = SegmentBytes[0],
                ["three"] = SegmentBytes[2],
            },
            useCachedYencStreams: true,
            SegmentIds.Zip(SegmentRanges).ToDictionary(pair => pair.First, pair => pair.Second),
            localSegments: new Dictionary<string, byte[]> { ["two"] = SegmentBytes[1] });
        using var _ = SetBudget(LargeBudget);
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, 4, SegmentRanges, knownMissingSegmentIndices: new HashSet<int> { 1 });
        stream.Seek(5, SeekOrigin.Begin);

        var buffer = new byte[5];
        Assert.Equal(5, await stream.ReadAsync(buffer));
        Assert.Equal("fghij", Encoding.ASCII.GetString(buffer));
        Assert.Equal(0, client.BodyRequestCounts.GetValueOrDefault("two"));
    }

    [Fact]
    public async Task ExactIndexedSeek_MissingPrimaryUsesValidatedFallback()
    {
        var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = SegmentBytes[0],
                ["two-fallback"] = SegmentBytes[1],
                ["three"] = SegmentBytes[2],
            },
            useCachedYencStreams: true,
            new Dictionary<string, LongRange>
            {
                ["one"] = SegmentRanges[0],
                ["two-fallback"] = SegmentRanges[1],
                ["three"] = SegmentRanges[2],
            });
        using var _ = SetBudget(LargeBudget);
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, 4, SegmentRanges,
            segmentFallbacks: [[], ["two-fallback"], []]);
        stream.Seek(5, SeekOrigin.Begin);

        var buffer = new byte[5];
        Assert.Equal(5, await stream.ReadAsync(buffer));
        Assert.Equal("fghij", Encoding.ASCII.GetString(buffer));
        Assert.Contains("two-fallback", client.RequestedSegmentIds);
    }

    [Fact]
    public async Task ExactIndexedSeek_CorruptionBeforeEmissionPreservesRetryPolicy()
    {
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "two"
                ? new ThrowingPhaseStream(new UsenetCorruptArticleException(
                    id, "provider-a", new InvalidDataException("CRC mismatch")))
                : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(6, SeekOrigin.Begin);

        var thrown = await Record.ExceptionAsync(async () => await stream.ReadAsync(new byte[1]));
        Assert.True(client.BodyRequestCounts["two"] > 1);
        Assert.True(
            thrown is null
            || thrown is UsenetCorruptArticleException
            || thrown is PersistentUsenetCorruptionException
            || thrown.InnerException is UsenetCorruptArticleException,
            thrown?.GetType().FullName);
    }

    [Fact]
    public async Task ExactIndexedSeek_CorruptionAfterEmissionAbortsAndDisposesRemainder()
    {
        var budget = new InFlightArticleBudget(1024 * 1024);
        var staged = new StagedBodyStream(
            "f"u8.ToArray(),
            "g"u8.ToArray(),
            "hij"u8.ToArray(),
            readFailure: phase => phase == "tail"
                ? new UsenetCorruptArticleException(
                    "two", "provider-a", new InvalidDataException("CRC mismatch"))
                : null);
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "two" ? staged : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        var stream = new NzbFileStream(
            SegmentIds, 15, client, 4, SegmentRanges, inFlightArticleBudget: budget);
        stream.Seek(6, SeekOrigin.Begin);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.BatchRequestCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        staged.ReleaseTail();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var buffer = new byte[8];
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        });

        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
        Assert.True(staged.AsyncDisposeCount + staged.SyncDisposeCount >= 1);
    }

    [Fact]
    public async Task ExactIndexedSeek_TransientFailureBeforeFirstByteStartsNoRemainder()
    {
        var client = CreateClient(decodedStreamFactory: (id, bytes) =>
            id == "two"
                ? new ThrowingPhaseStream(new IOException("transient body failure"))
                : new MemoryStream(bytes, writable: false));
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(6, SeekOrigin.Begin);

        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadAsync(new byte[1]));
        Assert.Equal(0, client.BatchRequestCount);
    }

    [Fact]
    public async Task ExactIndexedSeek_CancellationDuringPrefixDiscardStartsNoRemainder()
    {
        using var cts = new CancellationTokenSource();
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(6, SeekOrigin.Begin);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.ReadAsync(new byte[1], cts.Token));
        Assert.Equal(0, client.BatchRequestCount);
    }

    [Fact]
    public async Task SeekThenDisposeAsync_ReleasesHeadRemainderAndArticleBudget()
    {
        var budget = new InFlightArticleBudget(1024 * 1024);
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        var stream = new NzbFileStream(
            SegmentIds, 15, client, 4, SegmentRanges, inFlightArticleBudget: budget);
        stream.Seek(6, SeekOrigin.Begin);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.BatchRequestCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task ExactIndexedSeek_CancellationAfterHandoffReleasesAllBodies()
    {
        using var cts = new CancellationTokenSource();
        var budget = new InFlightArticleBudget(1024 * 1024);
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        var stream = new NzbFileStream(
            SegmentIds, 15, client, 4, SegmentRanges, inFlightArticleBudget: budget);
        stream.Seek(6, SeekOrigin.Begin);
        Assert.Equal(1, await stream.ReadAsync(new byte[1], cts.Token));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.BatchRequestCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await cts.CancelAsync();
        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task Seek_ReplacesStartedHandoffAndAwaitsCleanupBeforeNextBody()
    {
        var budget = new InFlightArticleBudget(1024 * 1024);
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = new NzbFileStream(
            SegmentIds, 15, client, 4, SegmentRanges, inFlightArticleBudget: budget);
        stream.Seek(6, SeekOrigin.Begin);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.BatchRequestCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        stream.Seek(11, SeekOrigin.Begin);
        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'l', buffer[0]);
        Assert.True(client.BodyRequestCounts.ContainsKey("three"));
        await stream.DisposeAsync();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Theory]
    [InlineData(0, 4, "abcd")]
    [InlineData(3, 4, "defg")]
    [InlineData(4, 6, "efghij")]
    [InlineData(9, 6, "jklmno")]
    [InlineData(14, 1, "o")]
    public async Task ExactIndexedSeek_RangeVariantsAreByteIdentical(
        long start, int count, string expected)
    {
        var client = CreateClient();
        using var _ = SetBudget(LargeBudget);
        await using var stream = CreateStream(client, articleBufferSize: 4);
        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0) break;
            read += n;
        }

        Assert.Equal(expected.Length, read);
        Assert.Equal(expected, Encoding.ASCII.GetString(buffer, 0, read));
        Assert.Equal(start + read, stream.Position);
    }

    private static FakeNntpClient CreateClient(
        Func<string, byte[], Stream>? decodedStreamFactory = null) =>
        new(
            SegmentIds.Zip(SegmentBytes).ToDictionary(pair => pair.First, pair => pair.Second),
            useCachedYencStreams: true,
            SegmentIds.Zip(SegmentRanges).ToDictionary(pair => pair.First, pair => pair.Second),
            decodedStreamFactory);

    private static NzbFileStream CreateStream(FakeNntpClient client, int articleBufferSize = 4) =>
        new(SegmentIds, 15, client, articleBufferSize, SegmentRanges);

    private static BudgetScope SetBudget(long? budget)
    {
        var previous = RangeContext.GetReadBudget();
        RangeContext.SetReadBudget(budget);
        return new BudgetScope(previous);
    }

    private readonly struct BudgetScope(long? previous) : IDisposable
    {
        public void Dispose() => RangeContext.SetReadBudget(previous);
    }

    private sealed class ImmediateEofStream : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new EndOfStreamException("truncated article");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new EndOfStreamException("truncated article"));

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Disposed = true;
            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private sealed class ThrowingPhaseStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw exception;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
