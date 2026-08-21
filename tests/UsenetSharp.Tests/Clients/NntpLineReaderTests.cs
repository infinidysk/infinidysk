using System.Text;
using UsenetSharp.Clients;
using UsenetSharp.Exceptions;
using UsenetSharpTest.Support;

namespace UsenetSharpTest.Protocol;

[TestFixture]
public class NntpLineReaderTests
{
    [Test]
    public async Task ReadLineBytesAsync_CancelledRefillDoesNotReplayConsumedBuffer()
    {
        await using var stream = new CancelledRefillStream(
            "first response\r\n",
            "second response\r\n");
        using var reader = new NntpLineReader(stream);

        var first = await reader.ReadLineAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelledRead = reader.ReadLineAsync(cancellation.Token).AsTask();
        await stream.RefillStarted.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await cancelledRead);
        var second = await reader.ReadLineAsync(CancellationToken.None);

        Assert.That(first, Is.EqualTo("first response"));
        Assert.That(second, Is.EqualTo("second response"));
    }

    [Test]
    public async Task ReadLineBytesAsync_EofWithUnterminatedLine_Throws()
    {
        await using var stream = new MemoryStream(Encoding.Latin1.GetBytes("22"));
        using var reader = new NntpLineReader(stream);

        var exception = Assert.ThrowsAsync<UsenetProtocolException>(async () =>
            await reader.ReadLineAsync(CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("unterminated line"));
    }

    [Test]
    public async Task ReadLineBytesAsync_CleanEofAfterCompleteLine_ReturnsNull()
    {
        await using var stream = new MemoryStream(Encoding.Latin1.GetBytes("205 Goodbye\r\n"));
        using var reader = new NntpLineReader(stream);

        Assert.That(await reader.ReadLineAsync(CancellationToken.None), Is.EqualTo("205 Goodbye"));
        Assert.That(await reader.ReadLineAsync(CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task ReadCompleteLinesAsync_ExposesLargestCompletePrefixWithoutAdvancing()
    {
        var input = Encoding.ASCII.GetBytes("one\r\ntwo\r\nthr");
        await using var stream = new MemoryStream(input);
        using var reader = new NntpLineReader(stream, bufferSize: 32);

        var first = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        Assert.That(first.HasValue, Is.True);
        Assert.That(Encoding.ASCII.GetString(first!.Value.Memory.Span), Is.EqualTo("one\r\ntwo\r\n"));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reader.ReadCompleteLinesAsync(CancellationToken.None));
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reader.ReadLineBytesAsync(CancellationToken.None));

        reader.Advance("one\r\n".Length);
        var second = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        Assert.That(Encoding.ASCII.GetString(second!.Value.Memory.Span), Is.EqualTo("two\r\n"));

        reader.Advance("two\r\n".Length);
        Assert.ThrowsAsync<UsenetProtocolException>(async () =>
            await reader.ReadCompleteLinesAsync(CancellationToken.None));
    }

    [Test]
    public async Task ReadCompleteLinesAsync_AssemblesCrLfSplitAcrossBuffers()
    {
        var input = Encoding.ASCII.GetBytes("x\r\n");
        await using var stream = new MemoryStream(input);
        using var reader = new NntpLineReader(stream, bufferSize: 2);

        var batch = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        Assert.That(Encoding.ASCII.GetString(batch!.Value.Memory.Span), Is.EqualTo("x\r\n"));
        reader.Advance(batch.Value.Memory.Length);
        Assert.That(await reader.ReadCompleteLinesAsync(CancellationToken.None), Is.Null);
    }

    [TestCase("=ybegin line=128 size=1 name=test.bin\r\n")]
    [TestCase("=ypart begin=1 end=1\r\n")]
    [TestCase("=yend size=1 crc32=89abcdef\r\n")]
    public async Task ReadCompleteLinesAsync_AssemblesControlLineSplitAtEveryByte(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line);
        for (var split = 1; split < bytes.Length; split++)
        {
            await using var stream = new FragmentedReadStream(bytes, [split, int.MaxValue]);
            using var reader = new NntpLineReader(stream, bufferSize: Math.Max(split, 1));
            var batch = await reader.ReadCompleteLinesAsync(CancellationToken.None);
            Assert.That(
                Encoding.ASCII.GetString(batch!.Value.Memory.Span),
                Is.EqualTo(line),
                $"split={split}");
            reader.Advance(batch.Value.Memory.Length);
        }
    }

    [Test]
    public async Task ReadCompleteLinesAsync_AssemblesDotTerminatorSplitAtEveryByte()
    {
        const string terminator = ".\r\n";
        var bytes = Encoding.ASCII.GetBytes(terminator);
        for (var split = 1; split < bytes.Length; split++)
        {
            await using var stream = new FragmentedReadStream(bytes, [split, int.MaxValue]);
            using var reader = new NntpLineReader(stream, bufferSize: 1);
            var batch = await reader.ReadCompleteLinesAsync(CancellationToken.None);
            Assert.That(
                Encoding.ASCII.GetString(batch!.Value.Memory.Span),
                Is.EqualTo(terminator),
                $"split={split}");
            reader.Advance(batch.Value.Memory.Length);
        }
    }

    [Test]
    public async Task ReadCompleteLinesAsync_AssemblesCrLfDotAsSeparateFills()
    {
        var bytes = Encoding.ASCII.GetBytes("payload\r\n.\r\n");
        await using var stream = new FragmentedReadStream(bytes, [1]);
        using var reader = new NntpLineReader(stream, bufferSize: 1);

        var first = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        Assert.That(Encoding.ASCII.GetString(first!.Value.Memory.Span), Is.EqualTo("payload\r\n"));
        reader.Advance(first.Value.Memory.Length);

        var second = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        Assert.That(Encoding.ASCII.GetString(second!.Value.Memory.Span), Is.EqualTo(".\r\n"));
        reader.Advance(second.Value.Memory.Length);
    }

    [Test]
    public async Task Advance_RejectsNonLineBoundaryAndOutOfRangeCounts()
    {
        var input = Encoding.ASCII.GetBytes("one\r\ntwo\r\n");
        await using var stream = new MemoryStream(input);
        using var reader = new NntpLineReader(stream);

        var batch = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        Assert.That(batch.HasValue, Is.True);

        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Advance(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Advance(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Advance(batch!.Value.Memory.Length + 1));
        Assert.Throws<ArgumentException>(() => reader.Advance(1));

        reader.Advance(batch!.Value.Memory.Length);
        Assert.Throws<InvalidOperationException>(() => reader.Advance(1));
    }

    [Test]
    public async Task Advance_ThroughTerminator_PreservesFollowingResponse()
    {
        var input = Encoding.ASCII.GetBytes("last-line\r\n.\r\n222 0 <next@example> body\r\n");
        await using var stream = new MemoryStream(input);
        using var reader = new NntpLineReader(stream);

        var batch = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        var span = batch!.Value.Memory.Span;
        var terminatorAt = span.LastIndexOf(".\r\n"u8);
        Assert.That(terminatorAt, Is.GreaterThanOrEqualTo(0));
        reader.Advance(terminatorAt + 3);

        var next = await reader.ReadLineAsync(CancellationToken.None);
        Assert.That(next, Is.EqualTo("222 0 <next@example> body"));
    }

    [Test]
    public async Task ReadCompleteLinesAsync_ThenReadLineBytesAsync_PreservesUnreadBytes()
    {
        var input = Encoding.ASCII.GetBytes("alpha\r\nbeta\r\n");
        await using var stream = new MemoryStream(input);
        using var reader = new NntpLineReader(stream);

        var batch = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        reader.Advance("alpha\r\n".Length);
        var line = await reader.ReadLineAsync(CancellationToken.None);
        Assert.That(line, Is.EqualTo("beta"));
        Assert.That(batch!.Value.Memory.Length, Is.EqualTo("alpha\r\nbeta\r\n".Length));
    }

    [Test]
    public async Task ReadLineBytesAsync_ThenReadCompleteLinesAsync_PreservesUnreadBytes()
    {
        var input = Encoding.ASCII.GetBytes("alpha\r\nbeta\r\ngamma\r\n");
        await using var stream = new MemoryStream(input);
        using var reader = new NntpLineReader(stream);

        Assert.That(await reader.ReadLineAsync(CancellationToken.None), Is.EqualTo("alpha"));
        var batch = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        Assert.That(Encoding.ASCII.GetString(batch!.Value.Memory.Span), Is.EqualTo("beta\r\ngamma\r\n"));
        reader.Advance(batch.Value.Memory.Length);
    }

    [Test]
    public async Task ReadCompleteLinesAsync_CancelledRefillDoesNotReplayConsumedBytes()
    {
        var first = Encoding.ASCII.GetBytes("first\r\n");
        var second = Encoding.ASCII.GetBytes("second\r\n");
        var combined = new byte[first.Length + second.Length];
        first.CopyTo(combined, 0);
        second.CopyTo(combined, first.Length);

        await using var stream = new FragmentedReadStream(
            combined,
            [first.Length, second.Length],
            cancelOnRead: 2);
        using var reader = new NntpLineReader(stream, bufferSize: first.Length);

        var batch = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        Assert.That(Encoding.ASCII.GetString(batch!.Value.Memory.Span), Is.EqualTo("first\r\n"));
        reader.Advance(batch.Value.Memory.Length);

        using var cancellation = new CancellationTokenSource();
        var cancelledRead = reader.ReadCompleteLinesAsync(cancellation.Token).AsTask();
        await stream.RefillStarted.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await cancelledRead);

        var line = await reader.ReadLineAsync(CancellationToken.None);
        Assert.That(line, Is.EqualTo("second"));
    }

    [Test]
    public async Task ReadCompleteLinesAsync_EofWithUnterminatedLine_Throws()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("22"));
        using var reader = new NntpLineReader(stream);

        var exception = Assert.ThrowsAsync<UsenetProtocolException>(async () =>
            await reader.ReadCompleteLinesAsync(CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("unterminated line"));
    }

    [Test]
    public async Task ReadCompleteLinesAsync_CleanEofAtBoundary_ReturnsNull()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("205 Goodbye\r\n"));
        using var reader = new NntpLineReader(stream);

        var batch = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        Assert.That(Encoding.ASCII.GetString(batch!.Value.Memory.Span), Is.EqualTo("205 Goodbye\r\n"));
        reader.Advance(batch.Value.Memory.Length);
        Assert.That(await reader.ReadCompleteLinesAsync(CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task ReadCompleteLinesAsync_OverlongBoundarySpanningLine_Throws()
    {
        var line = Encoding.ASCII.GetBytes(new string('a', 9) + "\r\n");
        await using var stream = new FragmentedReadStream(line, [3, 3, 5]);
        using var reader = new NntpLineReader(stream, maximumLineLength: 8, bufferSize: 3);

        var exception = Assert.ThrowsAsync<UsenetProtocolException>(async () =>
            await reader.ReadCompleteLinesAsync(CancellationToken.None));
        Assert.That(exception!.Message, Does.Contain("8-byte limit"));
    }

    [Test]
    public async Task Dispose_WithOutstandingExposure_ReturnsEachPooledArrayOnce()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("one\r\ntwo\r\n"));
        var reader = new NntpLineReader(stream, bufferSize: 4);
        var batch = await reader.ReadCompleteLinesAsync(CancellationToken.None);
        Assert.That(batch.HasValue, Is.True);

        reader.Dispose();
        reader.Dispose();
        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await reader.ReadCompleteLinesAsync(CancellationToken.None));
    }

    private sealed class CancelledRefillStream(
        string firstResponse,
        string secondResponse) : Stream
    {
        private readonly byte[] _firstResponse = Encoding.ASCII.GetBytes(firstResponse);
        private readonly byte[] _secondResponse = Encoding.ASCII.GetBytes(secondResponse);
        private readonly TaskCompletionSource _refillStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public Task RefillStarted => _refillStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            switch (Interlocked.Increment(ref _readCount))
            {
                case 1:
                    _firstResponse.AsSpan().CopyTo(buffer.Span);
                    return _firstResponse.Length;
                case 2:
                    _refillStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 0;
                case 3:
                    _secondResponse.AsSpan().CopyTo(buffer.Span);
                    return _secondResponse.Length;
                default:
                    return 0;
            }
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
