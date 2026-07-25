using System.Text;
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
    public async Task PaddedLengthStream_FillsPrematureEofToConfiguredLength()
    {
        await using var stream = new PaddedLengthStream(
            new MemoryStream(Encoding.ASCII.GetBytes("ab")), 5, "part-1", "test.bin");

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal(new byte[] { (byte)'a', (byte)'b', 0, 0, 0 }, destination.ToArray());
        Assert.Equal(5, stream.Position);
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
    }

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
            "test.bin");

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
                new MemoryStream(Encoding.ASCII.GetBytes("ab")), 4, "part-1", "test.bin")),
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
}
