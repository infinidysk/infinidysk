using System;
using System.IO;

namespace SharpCompress.IO;

internal sealed partial class SeekableSharpCompressStream : SharpCompressStream
{
    public override Stream BaseStream() => _stream;

    private readonly Stream _stream;
    private long? _recordedPosition;
    private bool _isDisposed;

    /// <summary>
    /// Gets or sets whether to leave the underlying stream open when disposed.
    /// </summary>
    public override bool LeaveStreamOpen { get; }

    public SeekableSharpCompressStream(Stream stream, bool leaveStreamOpen = false)
        : base(Null, leaveStreamOpen: true, bufferSize: null)
    {
        ThrowHelper.ThrowIfNull(stream);
        if (stream is SharpCompressStream)
        {
            throw new ArgumentException(
                "A SharpCompressStream must not be wrapped in SeekableSharpCompressStream.",
                nameof(stream)
            );
        }
        if (!stream.CanSeek)
        {
            throw new ArgumentException("Stream must be seekable", nameof(stream));
        }

        LeaveStreamOpen = leaveStreamOpen;
        _stream = stream;
    }

    public override bool CanRead => _stream.CanRead;

    public override bool CanSeek => _stream.CanSeek;

    public override bool CanWrite => _stream.CanWrite;

    public override long Length => _stream.Length;

    public override long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    internal override bool TrySetBufferedPosition(long targetPosition)
    {
        if (targetPosition < 0)
        {
            return false;
        }

        _stream.Position = targetPosition;
        return true;
    }

    internal override bool TrySkipForward(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        // Defensive delegation: the constructor rejects this today, but keeping
        // the guard here prevents a future construction path from treating a
        // ring-buffered wrapper as natively seekable.
        if (_stream is SharpCompressStream sharpCompressStream)
        {
            return sharpCompressStream.TrySkipForward(count);
        }

        _stream.Position += count;
        return true;
    }

    internal override bool IsRecording => _recordedPosition.HasValue;

    public override void Flush() => _stream.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        _stream.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _stream.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);

    public override void SetLength(long value) => _stream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
        _stream.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _stream.Write(buffer);

    public override void Rewind(bool stopRecording = false)
    {
        if (!_recordedPosition.HasValue)
        {
            return;
        }

        _stream.Seek(_recordedPosition.Value, SeekOrigin.Begin);
        if (stopRecording)
        {
            _recordedPosition = null;
        }
    }

    public override void StartRecording(int? minBufferSize = null) =>
        _recordedPosition = _stream.Position;

    public override void StopRecording() => _recordedPosition = null;

    public override void FreezeAndReleaseBuffer() => _recordedPosition = null;

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        if (disposing && !LeaveStreamOpen)
        {
            _stream.Dispose();
        }
        base.Dispose(disposing);
    }
}
