using System;
using System.IO;
using SharpCompress.Common;

namespace SharpCompress.IO;

/// <summary>
/// Zero-overhead passthrough wrapper: all I/O delegates to the underlying stream with no ring buffer.
/// Created by <see cref="SharpCompressStream.CreateNonDisposing"/>.
/// </summary>
internal sealed partial class PassthroughSharpCompressStream : SharpCompressStream
{
    public PassthroughSharpCompressStream(Stream stream)
        : base(stream, leaveStreamOpen: true, bufferSize: null) { }

    internal override bool IsPassthrough => true;

    public override bool CanSeek => stream.CanSeek;

    public override bool CanWrite => stream.CanWrite;

    public override void Flush() => stream.Flush();

    public override long Length => stream.Length;

    public override long Position
    {
        get => stream.Position;
        set => stream.Position = value;
    }

    internal override bool TrySetBufferedPosition(long targetPosition)
    {
        if (!stream.CanSeek)
        {
            return false;
        }

        stream.Position = targetPosition;
        return true;
    }

    internal override bool TrySkipForward(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (stream is SharpCompressStream sharpCompressStream)
        {
            return sharpCompressStream.TrySkipForward(count);
        }

        if (!stream.CanSeek)
        {
            return false;
        }

        stream.Position += count;
        return true;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        stream.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => stream.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);

    public override void SetLength(long value) => stream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
        stream.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => stream.Write(buffer);

    public override void Rewind(bool stopRecording) =>
        throw new ArchiveOperationException(
            "Rewind cannot be called on a passthrough stream. Use Create() first."
        );

    public override void StopRecording() =>
        throw new ArchiveOperationException(
            "StopRecording cannot be called on a passthrough stream. Use Create() first."
        );

    public override void FreezeAndReleaseBuffer() =>
        throw new ArchiveOperationException(
            "FreezeAndReleaseBuffer cannot be called on a passthrough stream. Use Create() first."
        );

    public override void StartRecording(int? minBufferSize = null) =>
        throw new ArchiveOperationException(
            "StartRecording cannot be called on a passthrough stream. Use Create() first."
        );
}
