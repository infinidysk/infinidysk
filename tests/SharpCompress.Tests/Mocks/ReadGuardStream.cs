using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Test.Mocks;

/// <summary>
/// Seekable test stream that can reject reads before an absolute position.
/// </summary>
public sealed class ReadGuardStream(Stream stream) : Stream
{
    private long? _readsAllowedAtOrAfter;

    public void RejectReadsBefore(long position) => _readsAllowedAtOrAfter = position;

    public override bool CanRead => stream.CanRead;
    public override bool CanSeek => stream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => stream.Length;

    public override long Position
    {
        get => stream.Position;
        set => stream.Position = value;
    }

    public override void Flush() => stream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfReadRejected();
        return stream.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfReadRejected();
        return stream.Read(buffer);
    }

    public override int ReadByte()
    {
        ThrowIfReadRejected();
        return stream.ReadByte();
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        ThrowIfReadRejected();
        return stream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfReadRejected();
        return stream.ReadAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stream.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfReadRejected()
    {
        if (_readsAllowedAtOrAfter is long position && Position < position)
        {
            throw new InvalidOperationException(
                $"Read attempted at position {Position} before allowed position {position}."
            );
        }
    }
}
