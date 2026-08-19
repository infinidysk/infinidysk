using System.Text;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class BasicStreamTests
{
    [Fact]
    public async Task DiscardExactBytesAsync_RejectsAStreamThatEndsEarly()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        var failure = await Assert.ThrowsAsync<EndOfStreamException>(
            () => stream.DiscardExactBytesAsync(5));

        Assert.Contains("2 bytes before 5", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscardExactBytesAsync_SkipsTheRequestedPrefix()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abcdefgh"));

        await stream.DiscardExactBytesAsync(3);

        var remaining = new byte[5];
        Assert.Equal(5, await stream.ReadAsync(remaining));
        Assert.Equal("defgh", Encoding.ASCII.GetString(remaining));
    }

    [Fact]
    public async Task DiscardBytesAsync_ToleratesAStreamThatEndsEarly()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        await stream.DiscardBytesAsync(5);

        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task LimitedLengthStream_StopsAtConfiguredLength()
    {
        await using var stream = new LimitedLengthStream(
            new MemoryStream(Encoding.ASCII.GetBytes("abcdefgh")), 5);

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal("abcde", Encoding.ASCII.GetString(destination.ToArray()));
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task PaddedLengthStream_EncryptedPart_FillsPrematureEofToConfiguredLength()
    {
        await using var stream = new PaddedLengthStream(
            new MemoryStream(Encoding.ASCII.GetBytes("ab")), 5, "part-1", "test.bin",
            EncryptedPartContext());

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal(new byte[] { (byte)'a', (byte)'b', 0, 0, 0 }, destination.ToArray());
        Assert.Equal(5, stream.Position);
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task PaddedLengthStream_UnencryptedShortPart_FailsWithPartContext()
    {
        await using var stream = new PaddedLengthStream(
            new MemoryStream(Encoding.ASCII.GetBytes("ab")), 5, "part-1", "test.bin",
            EncryptedPartContext() with { IsEncrypted = false });

        var failure = await Assert.ThrowsAsync<IncompleteMultipartPartException>(
            () =>
            {
                using var destination = new MemoryStream();
                return stream.CopyToAsync(destination);
            });

        Assert.Contains("ended 3 bytes early", failure.Message, StringComparison.Ordinal);
        Assert.Contains("delivered 2 of 5 expected bytes", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Part 2 of 3", failure.Message, StringComparison.Ordinal);
        Assert.Contains("declared volume length 4096", failure.Message, StringComparison.Ordinal);
        Assert.Contains("read from offset 128", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaddedLengthStream_EncryptedPart_FailsWhenTheShortfallExceedsAnAesBlock()
    {
        await using var stream = new PaddedLengthStream(
            new MemoryStream(Encoding.ASCII.GetBytes("ab")), 1024, "part-1", "test.bin",
            EncryptedPartContext());

        var failure = await Assert.ThrowsAsync<IncompleteMultipartPartException>(
            () =>
            {
                using var destination = new MemoryStream();
                return stream.CopyToAsync(destination);
            });

        Assert.Contains("ended 1022 bytes early", failure.Message, StringComparison.Ordinal);
        Assert.Contains("encrypted: True", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaddedLengthStream_WithoutPartContext_DoesNotInventBytes()
    {
        await using var stream = new PaddedLengthStream(
            new MemoryStream(Encoding.ASCII.GetBytes("ab")), 5, "part-1", "test.bin");

        await Assert.ThrowsAsync<IncompleteMultipartPartException>(
            () =>
            {
                using var destination = new MemoryStream();
                return stream.CopyToAsync(destination);
            });
    }

    private static MultipartPartContext EncryptedPartContext() => new()
    {
        PartNumber = 2,
        PartCount = 3,
        SeekOffsetWithinPart = 128,
        DeclaredVolumeLength = 4096,
        IsEncrypted = true,
    };

    [Theory]
    [InlineData("", 0)]
    [InlineData("", 3)]
    [InlineData("abc", 3)]
    public async Task PaddedLengthStream_HandlesEmptyAndExactLengthInputs(
        string content, int declaredLength)
    {
        await using var stream = new PaddedLengthStream(
            new MemoryStream(Encoding.ASCII.GetBytes(content)),
            declaredLength,
            "part-1",
            "test.bin",
            EncryptedPartContext());

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        var expected = new byte[declaredLength];
        Encoding.ASCII.GetBytes(content).CopyTo(expected, 0);
        Assert.Equal(expected, destination.ToArray());
    }

    [Fact]
    public async Task CombinedStream_PaddedShortPartPreservesFollowingPartOffset()
    {
        var streams = new[]
        {
            Task.FromResult<Stream>(new PaddedLengthStream(
                new MemoryStream(Encoding.ASCII.GetBytes("ab")), 4, "part-1", "test.bin",
                EncryptedPartContext())),
            Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("cd")))
        };
        await using var stream = new CombinedStream(streams);

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal(new byte[] { (byte)'a', (byte)'b', 0, 0, (byte)'c', (byte)'d' },
            destination.ToArray());
        Assert.Equal(6, stream.Position);
    }

    [Fact]
    public async Task CombinedStream_ConcatenatesEmptyAndNonEmptyStreams()
    {
        var streams = new[]
        {
            Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("abc"))),
            Task.FromResult<Stream>(new MemoryStream()),
            Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("def")))
        };
        await using var stream = new CombinedStream(streams);

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal("abcdef", Encoding.ASCII.GetString(destination.ToArray()));
        Assert.Equal(6, stream.Position);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("x", false)]
    [InlineData("payload", false)]
    public async Task ProbingStream_ReportsEmptinessWithoutConsumingData(
        string content, bool expectedEmpty)
    {
        await using var stream = new ProbingStream(
            new MemoryStream(Encoding.UTF8.GetBytes(content)));

        Assert.Equal(expectedEmpty, await stream.IsEmptyAsync());
        Assert.Equal(expectedEmpty, await stream.IsEmptyAsync());

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        Assert.Equal(content, Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public async Task CancellableStream_RejectsReadsAfterCancellation()
    {
        using var cts = new CancellationTokenSource();
        await using var stream = new CancellableStream(
            new MemoryStream(new byte[] { 1, 2, 3 }), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.ReadAsync(new byte[1]));
    }

    [Fact]
    public void CancellableStream_SynchronousReadsAreNotSupported()
    {
        using var stream = new CancellableStream(
            new MemoryStream(new byte[] { 1, 2, 3 }), CancellationToken.None);

        var arrayEx = Assert.Throws<NotSupportedException>(
            () => stream.Read(new byte[1], 0, 1));
        Assert.Equal("CancellableStream supports asynchronous reads only.", arrayEx.Message);

        var spanEx = Assert.Throws<NotSupportedException>(
            () => stream.Read((Span<byte>)new byte[1]));
        Assert.Equal("CancellableStream supports asynchronous reads only.", spanEx.Message);
    }

    [Fact]
    public async Task CancellableStream_ReadAsyncHonorsConstructionTokenWhenCallerTokenIsLinked()
    {
        using var constructionCts = new CancellationTokenSource();
        using var callerCts = new CancellationTokenSource();
        var hang = new HangUntilCancelledStream();
        await using var stream = new CancellableStream(hang, constructionCts.Token);

        var readTask = stream.ReadAsync(new byte[1], callerCts.Token).AsTask();
        Assert.True(hang.Started.Wait(TimeSpan.FromSeconds(5)));
        await constructionCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    private sealed class HangUntilCancelledStream : Stream
    {
        public ManualResetEventSlim Started { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.Set();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) Started.Dispose();
            base.Dispose(disposing);
        }
    }
}
