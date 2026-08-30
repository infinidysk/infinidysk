using System.Net;
using System.Text;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class HttpContentReadUtilTests
{
    private const long Limit = 16;

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    public async Task ReadBoundedAsync_AcceptsBodiesAtOrBelowLimit(int size)
    {
        var payload = Enumerable.Repeat((byte)'a', size).ToArray();
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentLength = payload.Length;

        var bytes = await HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None);

        Assert.Equal(payload, bytes);
    }

    [Fact]
    public async Task ReadBoundedAsync_RejectsDeclaredContentLengthAboveLimit()
    {
        using var content = new ByteArrayContent(new byte[Limit + 1]);
        content.Headers.ContentLength = Limit + 1;

        var ex = await Assert.ThrowsAsync<NzbResponseTooLargeException>(
            () => HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None));

        Assert.Equal(Limit, ex.MaxBytes);
        Assert.Equal(Limit + 1, ex.ContentLength);
    }

    [Fact]
    public async Task ReadBoundedAsync_RejectsStreamedBodyAboveLimitWithoutPartialReturn()
    {
        using var content = new UndeclaredLengthContent(new byte[Limit + 1]);

        var ex = await Assert.ThrowsAsync<NzbResponseTooLargeException>(
            () => HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None));

        Assert.Equal(Limit, ex.MaxBytes);
        Assert.Null(ex.ContentLength);
    }

    [Fact]
    public async Task ReadBoundedAsync_AcceptsUndeclaredBodyAtLimit()
    {
        var payload = Encoding.ASCII.GetBytes("0123456789abcdef");
        Assert.Equal(Limit, payload.Length);
        using var content = new UndeclaredLengthContent(payload);

        var bytes = await HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None);

        Assert.Equal(payload, bytes);
    }

    [Fact]
    public async Task ReadBoundedAsync_RejectsLyingSmallDeclarationByStreamCount()
    {
        var payload = new byte[Limit + 1];
        Array.Fill(payload, (byte)'a');
        var stream = new TrackingStream(payload);
        using var content = new StreamHttpContent(stream, declaredLength: Limit - 1);

        var ex = await Assert.ThrowsAsync<NzbResponseTooLargeException>(
            () => HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None));

        Assert.Equal(Limit, ex.MaxBytes);
        Assert.Null(ex.ContentLength);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ReadBoundedAsync_AcceptsDeclaredAtLimitWithShorterActualBody()
    {
        var payload = Encoding.ASCII.GetBytes("hello");
        using var content = new StreamHttpContent(new TrackingStream(payload), declaredLength: Limit);

        var bytes = await HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None);

        Assert.Equal(payload, bytes);
    }

    [Fact]
    public async Task ReadBoundedAsync_RejectsDeclaredOversizeBeforeOpeningStream()
    {
        using var content = new FailIfOpenedContent(Limit + 1);

        var ex = await Assert.ThrowsAsync<NzbResponseTooLargeException>(
            () => HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None));

        Assert.Equal(Limit, ex.MaxBytes);
        Assert.Equal(Limit + 1, ex.ContentLength);
        Assert.False(content.StreamOpened);
    }

    [Fact]
    public async Task ReadBoundedAsync_NonSeekableUndeclared_ExactLimitSucceedsAndOneOverRejects()
    {
        var exact = Enumerable.Repeat((byte)'x', (int)Limit).ToArray();
        var stream = new TrackingStream(exact);
        using (var content = new StreamHttpContent(stream))
        {
            var bytes = await HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None);
            Assert.Equal(exact, bytes);
            Assert.True(stream.Disposed);
        }

        var over = new TrackingStream(Enumerable.Repeat((byte)'x', (int)Limit + 1).ToArray());
        using var overContent = new StreamHttpContent(over);
        await Assert.ThrowsAsync<NzbResponseTooLargeException>(
            () => HttpContentReadUtil.ReadBoundedAsync(overContent, Limit, CancellationToken.None));
        Assert.True(over.Disposed);
    }

    [Fact]
    public async Task ReadBoundedAsync_CancellationDuringBlockedRead_IsCancellationNotSize()
    {
        using var cts = new CancellationTokenSource();
        var stream = new BlockingReadStream();
        using var content = new StreamHttpContent(stream);

        var read = HttpContentReadUtil.ReadBoundedAsync(content, Limit, cts.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ReadBoundedAsync_OversizePrefix_StopsAtLimitPlusOneByte()
    {
        var payload = Enumerable.Repeat((byte)'a', 1 << 16).ToArray();
        var stream = new TrackingStream(payload);
        using var content = new StreamHttpContent(stream);

        await Assert.ThrowsAsync<NzbResponseTooLargeException>(
            () => HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None));

        Assert.True(stream.ReadCount <= Limit + 1, $"read {stream.ReadCount} bytes past the proving byte");
        Assert.True(stream.Disposed);
    }

    private sealed class UndeclaredLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(payload, 0, payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
