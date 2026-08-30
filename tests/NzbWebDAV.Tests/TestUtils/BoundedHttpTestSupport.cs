using System.Net;

namespace NzbWebDAV.Tests.TestUtils;

internal sealed class ScriptedHandler(
    Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
    : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request, cancellationToken));
    }
}

internal sealed class TrackingStream(byte[] payload, int maxRead = int.MaxValue) : Stream
{
    private int _offset;
    public int ReadCount { get; private set; }
    public bool Disposed { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = ReadCore(buffer.AsSpan(offset, count));
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadCore(buffer.Span));
    }

    private int ReadCore(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (_offset >= payload.Length) return 0;
        var take = Math.Min(Math.Min(buffer.Length, payload.Length - _offset), maxRead);
        payload.AsSpan(_offset, take).CopyTo(buffer);
        _offset += take;
        ReadCount += take;
        return take;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class PrefixThenBlockStream(byte[] prefix) : Stream
{
    private int _offset;
    public bool EnteredWait { get; private set; }
    public bool Disposed { get; private set; }
    public int BytesDelivered { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadCore(buffer.AsSpan(offset, count), CancellationToken.None);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < prefix.Length)
            return ValueTask.FromResult(ReadCore(buffer.Span, cancellationToken));
        return WaitAsync(cancellationToken);
    }

    private int ReadCore(Span<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (_offset >= prefix.Length)
        {
            EnteredWait = true;
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }

        var take = Math.Min(buffer.Length, prefix.Length - _offset);
        prefix.AsSpan(_offset, take).CopyTo(buffer);
        _offset += take;
        BytesDelivered += take;
        return take;
    }

    private async ValueTask<int> WaitAsync(CancellationToken cancellationToken)
    {
        EnteredWait = true;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task.ConfigureAwait(false);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class BlockingReadStream : Stream
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Started.TrySetResult();
        CancellationToken.None.WaitHandle.WaitOne();
        return 0;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task.ConfigureAwait(false);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class StreamHttpContent(Stream stream, long? declaredLength = null) : HttpContent
{
    public Stream Stream { get; } = stream;

    protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) =>
        Stream.CopyToAsync(destination);

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(Stream);

    protected override bool TryComputeLength(out long length)
    {
        if (declaredLength is { } declared)
        {
            length = declared;
            return true;
        }

        length = 0;
        return false;
    }
}

internal sealed class FailIfOpenedContent : HttpContent
{
    public bool StreamOpened { get; private set; }

    public FailIfOpenedContent(long declaredLength)
    {
        Headers.ContentLength = declaredLength;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        StreamOpened = true;
        throw new InvalidOperationException("content stream should not be opened");
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        StreamOpened = true;
        throw new InvalidOperationException("content stream should not be opened");
    }

    protected override bool TryComputeLength(out long length)
    {
        length = Headers.ContentLength ?? 0;
        return Headers.ContentLength is not null;
    }
}
