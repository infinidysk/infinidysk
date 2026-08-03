using System;
using System.IO;
using SharpCompress.Common;

namespace SharpCompress.IO;

/// <summary>
/// Stream wrapper that provides optional ring-buffered reading for non-seekable
/// or forward-only streams, enabling limited backward seeking required by some
/// decompressors and archive formats.
/// </summary>
/// <remarks>
/// <para>
/// Prefer the static <see cref="Create(Stream, int?)"/> / <see cref="CreateNonDisposing"/>
/// factories over constructing this type directly. Factories select passthrough
/// (<c>PassthroughSharpCompressStream</c>), seekable-delegate
/// (<c>SeekableSharpCompressStream</c>), or ring-buffered mode and ownership rules
/// for the underlying stream. Each concrete type implements exactly one buffering strategy.
/// </para>
///
/// <para><b>Modes</b></para>
/// <list type="bullet">
/// <item>
/// <b>Passthrough</b> (<see cref="CreateNonDisposing"/> → <c>PassthroughSharpCompressStream</c>):
/// no ring buffer; reads/writes/seeks forward to the underlying stream.
/// <see cref="CanSeek"/> / <see cref="Position"/> / <see cref="Length"/> / write / flush all
/// delegate. Recording APIs are illegal (see below).
/// </item>
/// <item>
/// <b>Ring-buffered</b> (<see cref="Create(Stream, int?)"/> on a non-seekable stream → this type):
/// a <see cref="RingBuffer"/> stores bytes read from the underlying stream so limited backward
/// seeking and format-detection rewind are possible. <see cref="CanSeek"/> is true for buffered
/// position changes within the recorded/buffered window; write and flush are not supported.
/// </item>
/// <item>
/// <b>Seekable delegate</b> (<c>SeekableSharpCompressStream</c> from <see cref="Create(Stream, int?)"/>
/// on a seekable stream): no ring buffer; <see cref="Position"/> / seek / read / write delegate to
/// the underlying stream. <see cref="StartRecording"/> stores the native position;
/// <see cref="Rewind()"/> seeks back to it.
/// </item>
/// </list>
///
/// <para><b>Ownership / <see cref="LeaveStreamOpen"/></b></para>
/// <list type="bullet">
/// <item>
/// <see cref="CreateNonDisposing"/> always sets <see cref="LeaveStreamOpen"/> to
/// <see langword="true"/>: disposing the wrapper never disposes the underlying stream.
/// </item>
/// <item>
/// <see cref="Create(Stream, int?)"/> on a raw (not already wrapped) stream takes ownership
/// (<see cref="LeaveStreamOpen"/> is <see langword="false"/>): disposing the wrapper disposes
/// the underlying stream.
/// </item>
/// <item>
/// When <see cref="Create(Stream, int?)"/> unwraps a passthrough wrapper, the new wrapper
/// preserves non-owning semantics (<see cref="LeaveStreamOpen"/> is <see langword="true"/>)
/// so <see cref="CreateNonDisposing"/> callers do not unexpectedly lose the underlying stream.
/// </item>
/// </list>
///
/// <para><b><see cref="Position"/> semantics</b></para>
/// <list type="bullet">
/// <item>
/// Passthrough and seekable-delegate modes report and set the underlying stream's
/// <see cref="Stream.Position"/>.
/// </item>
/// <item>
/// Ring-buffered modes report <c>_logicalPosition</c>, which can lag the underlying read
/// cursor (<c>streamPosition</c>) after <see cref="Rewind()"/> / <see cref="StopRecording"/> /
/// <see cref="FreezeAndReleaseBuffer"/>.
/// Subsequent reads replay from the ring buffer until the logical cursor catches up, then
/// continue reading (and, until buffer release, recording into the ring) from the underlying stream.
/// </item>
/// </list>
///
/// <para><b>Recording rules (ring-buffered mode)</b></para>
/// <list type="bullet">
/// <item>
/// In ring-buffered mode, bytes read from the underlying stream are always written into the
/// ring (for over-read protection and rewind) until <see cref="FreezeAndReleaseBuffer"/>
/// frees it. <see cref="StartRecording"/> sets the rewind anchor; recording stays active until
/// <see cref="StopRecording"/>, <see cref="FreezeAndReleaseBuffer"/>, or
/// <see cref="Rewind(bool)"/> with <c>stopRecording: true</c>.
/// </item>
/// <item>
/// <see cref="StartRecording"/> anchors at the current underlying read cursor. When
/// <c>minBufferSize</c> is larger than the existing capacity, and a ring was already
/// allocated with a smaller capacity, <see cref="StartRecording"/> throws. When no ring
/// exists yet, capacity is at least <see cref="Common.Constants.RewindableBufferSize"/>
/// (or <c>minBufferSize</c> if larger).
/// </item>
/// <item>
/// <b>Frozen recording:</b> after <see cref="StopRecording"/> or
/// <see cref="Rewind(bool)"/> with <c>stopRecording: true</c>, <c>_isRecording</c> is
/// <see langword="false"/> but the recording anchor (<c>_recordingStartPosition</c>) is kept.
/// Further <see cref="Rewind()"/> calls and seeks within
/// <c>[anchor, streamPosition]</c> remain legal. The anchor is cleared only when a new
/// <see cref="StartRecording"/> begins, <see cref="FreezeAndReleaseBuffer"/> runs, or the
/// stream is disposed.
/// </item>
/// <item>
/// <b>Released buffer:</b> <see cref="FreezeAndReleaseBuffer"/> clears the recording anchor and
/// requests ring disposal. The ring is freed once <c>_logicalPosition</c> catches up to
/// <c>streamPosition</c>; subsequent reads pass straight through. After release,
/// <see cref="StartRecording"/>, <see cref="Rewind()"/>, and <see cref="StopRecording"/> throw.
/// </item>
/// </list>
///
/// <para><b>Legal call sequences</b></para>
/// <list type="bullet">
/// <item>
/// Typical detection flow: <c>StartRecording → (reads) → Rewind</c> (optionally repeated while
/// still recording) → <c>StopRecording</c> (or <c>Rewind(stopRecording: true)</c>) or
/// <c>FreezeAndReleaseBuffer</c> for allowlisted formats that no longer need rewind.
/// </item>
/// <item>
/// After freeze: <see cref="Rewind()"/> is still allowed; <see cref="StopRecording"/> throws
/// because recording is no longer active; <see cref="StartRecording"/> may begin a new session.
/// </item>
/// <item>
/// After <see cref="FreezeAndReleaseBuffer"/>: recording APIs throw; reads are direct once the
/// ring has been returned to the pool.
/// </item>
/// <item>
/// In <b>passthrough</b> mode, <see cref="StartRecording"/>, <see cref="Rewind()"/> /
/// <see cref="Rewind(bool)"/>, <see cref="StopRecording"/>, and
/// <see cref="FreezeAndReleaseBuffer"/> always throw
/// <see cref="ArchiveOperationException"/>.
/// </item>
/// <item>
/// <see cref="Rewind()"/> also throws if recording was never started, or if the recording
/// anchor has aged out of the ring buffer (overflow).
/// </item>
/// </list>
///
/// <para>
/// Factory unwrap / <c>bufferSize</c> ignore rules are documented on
/// <see cref="Create(Stream, int?)"/> and <see cref="CreateNonDisposing"/>.
/// </para>
/// </remarks>
public partial class SharpCompressStream : Stream, IStreamStack
{
    public virtual Stream BaseStream() => stream;

    // Underlying stream; subclasses that replace the buffering strategy (passthrough / seekable)
    // may still reference this field or supply their own.
    protected readonly Stream stream;
    private bool isDisposed;
    private long streamPosition;

    // Ring buffer for recording mode and over-read protection.
    // Single unified buffering mechanism for both use cases.
    private RingBuffer? _ringBuffer;
    private long _logicalPosition; // The current logical read position (can be behind streamPosition)

    // Recording state: anchor position when StartRecording was called
    private long? _recordingStartPosition;
    private bool _isRecording;

    // When true, the ring buffer is disposed once logical position catches up to streamPosition.
    private bool _bufferReleaseRequested;

    /// <summary>
    /// Gets whether this stream is in passthrough mode (no buffering, delegates to underlying stream).
    /// </summary>
    internal virtual bool IsPassthrough => false;

    /// <summary>
    /// Gets whether to leave the underlying stream open when disposed.
    /// </summary>
    public virtual bool LeaveStreamOpen { get; }

    public SharpCompressStream(Stream stream)
    {
        this.stream = stream;
        _logicalPosition = 0;
    }

    /// <summary>
    /// Constructor for ring-buffered mode and for subclasses that share the base stream field.
    /// </summary>
    protected SharpCompressStream(Stream stream, bool leaveStreamOpen, int? bufferSize)
    {
        this.stream = stream;
        LeaveStreamOpen = leaveStreamOpen;
        _logicalPosition = 0;

        if (bufferSize.HasValue && bufferSize.Value > 0)
        {
            _ringBuffer = new RingBuffer(bufferSize.Value);
        }
    }

    /// <summary>
    /// Gets whether the stream is actively recording reads to the ring buffer.
    /// </summary>
    internal virtual bool IsRecording => _isRecording;

    /// <summary>
    /// Gets whether a ring buffer is currently allocated.
    /// </summary>
    internal bool HasRingBuffer => _ringBuffer is not null;

    /// <summary>
    /// Gets whether <see cref="FreezeAndReleaseBuffer"/> has been called.
    /// </summary>
    internal bool IsBufferReleaseRequested => _bufferReleaseRequested;

    protected override void Dispose(bool disposing)
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        base.Dispose(disposing);
        if (disposing)
        {
            if (!LeaveStreamOpen)
            {
                stream.Dispose();
            }
            _ringBuffer?.Dispose();
            _ringBuffer = null;
        }
    }

    public void Rewind() => Rewind(false);

    public virtual void Rewind(bool stopRecording)
    {
        if (_bufferReleaseRequested)
        {
            throw new ArchiveOperationException(
                "Rewind cannot be called after FreezeAndReleaseBuffer()."
            );
        }

        if (_recordingStartPosition is null)
        {
            throw new ArchiveOperationException(
                "Rewind can only be called after StartRecording() has been called."
            );
        }

        // Verify recording anchor is within ring buffer range
        long anchorAge = streamPosition - _recordingStartPosition.Value;
        if (anchorAge > _ringBuffer!.Length)
        {
            throw new ArchiveOperationException(
                $"Cannot rewind: recording anchor is {anchorAge} bytes behind current position, "
                    + $"but ring buffer only holds {_ringBuffer.Length} bytes. "
                    + $"Recording buffer overflow - increase DefaultRollingBufferSize or reduce format detection reads."
            );
        }

        // Rewind logical position to recording anchor
        _logicalPosition = _recordingStartPosition.Value;

        if (stopRecording)
        {
            _isRecording = false;
            // Note: We keep _recordingStartPosition so Rewind() can be called again
            // (frozen recording mode). The anchor is only cleared when a new recording
            // starts, FreezeAndReleaseBuffer runs, or the stream is disposed.
        }
    }

    public virtual void StopRecording()
    {
        if (_bufferReleaseRequested)
        {
            throw new ArchiveOperationException(
                "StopRecording cannot be called after FreezeAndReleaseBuffer()."
            );
        }
        if (!IsRecording)
        {
            throw new ArchiveOperationException(
                "StopRecording can only be called when recording is active."
            );
        }

        // Mark that we're no longer actively recording
        _isRecording = false;

        // Rewind to recording anchor position
        _logicalPosition = _recordingStartPosition!.Value;

        // Note: We keep _recordingStartPosition so future Rewind() calls still work
        // (frozen recording mode). The anchor is only cleared when a new recording
        // starts, FreezeAndReleaseBuffer runs, or the stream is disposed.
    }

    /// <summary>
    /// Requests release of the ring buffer once all replayed bytes have been consumed.
    /// After release, reads pass straight through and Rewind/StartRecording/StopRecording throw.
    /// </summary>
    /// <remarks>
    /// When a recording anchor is present, the logical position is rewound to that anchor
    /// (same as <see cref="StopRecording"/>) so format-detection call sites can replace
    /// <see cref="StopRecording"/> with this method. The ring is disposed only after the
    /// logical cursor catches up to the underlying read cursor.
    /// </remarks>
    public virtual void FreezeAndReleaseBuffer()
    {
        if (_bufferReleaseRequested)
        {
            throw new ArchiveOperationException(
                "FreezeAndReleaseBuffer can only be called once before the buffer is released."
            );
        }

        // Mirror StopRecording: rewind to the detection anchor when one exists so callers that
        // previously used StopRecording keep the correct logical position for subsequent reads.
        if (_recordingStartPosition is not null)
        {
            _logicalPosition = _recordingStartPosition.Value;
        }

        _bufferReleaseRequested = true;
        _recordingStartPosition = null;
        _isRecording = false;

        TryReleaseRingBuffer();
    }

    /// <summary>
    /// Begins recording reads so that <see cref="Rewind()"/> can replay them.
    /// </summary>
    /// <param name="minBufferSize">
    /// Minimum ring buffer capacity in bytes. When provided and larger than
    /// <see cref="Common.Constants.RewindableBufferSize"/>, the ring buffer is allocated
    /// with this size. Pass the largest amount of compressed data that may be consumed
    /// during format detection before the first rewind. Defaults to
    /// <see cref="Common.Constants.RewindableBufferSize"/> when null or not supplied.
    /// </param>
    public virtual void StartRecording(int? minBufferSize = null)
    {
        if (_bufferReleaseRequested)
        {
            throw new ArchiveOperationException(
                "StartRecording cannot be called after FreezeAndReleaseBuffer()."
            );
        }
        if (IsRecording)
        {
            throw new ArchiveOperationException(
                "StartRecording can only be called when not already recording."
            );
        }

        // Allocate ring buffer with the requested minimum size (at least the global default).
        if (_ringBuffer is null)
        {
            var requiredSize =
                minBufferSize.GetValueOrDefault() > Constants.RewindableBufferSize
                    ? minBufferSize.GetValueOrDefault()
                    : Constants.RewindableBufferSize;
            _ringBuffer = new RingBuffer(requiredSize);
        }
        else if (minBufferSize.HasValue && minBufferSize.Value > _ringBuffer.Capacity)
        {
            throw new ArchiveOperationException(
                $"StartRecording requires a ring buffer of at least {minBufferSize.Value} bytes, but the stream was created with capacity {_ringBuffer.Capacity}."
            );
        }

        // Mark current position as recording anchor
        _recordingStartPosition = streamPosition;
        _logicalPosition = streamPosition;
        _isRecording = true;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override void Flush() => throw new NotSupportedException();

    public override long Length
    {
        get
        {
            if (_ringBuffer is not null)
            {
                return _ringBuffer.Length;
            }
            throw new NotSupportedException();
        }
    }

    public override long Position
    {
        get => _logicalPosition;
        set => SeekToPosition(value);
    }

    private void SeekToPosition(long targetPosition)
    {
        if (!TrySetBufferedPosition(targetPosition))
        {
            if (_recordingStartPosition is not null)
            {
                throw new NotSupportedException(
                    $"Cannot seek to position {targetPosition}. Valid recorded range: "
                        + $"[{_recordingStartPosition.Value}, {streamPosition}]"
                );
            }

            if (_ringBuffer is not null)
            {
                long ringBufferStart = streamPosition - _ringBuffer.Length;
                throw new NotSupportedException(
                    $"Cannot seek to position {targetPosition}. Valid ring buffer range: "
                        + $"[{ringBufferStart}, {streamPosition}]"
                );
            }

            throw new NotSupportedException("Cannot seek on non-buffered stream.");
        }
    }

    /// <summary>
    /// Attempts to set <see cref="Position"/> within the buffered or recorded range without throwing.
    /// </summary>
    internal virtual bool TrySetBufferedPosition(long targetPosition)
    {
#if DEBUG
        if (_bufferReleaseRequested && targetPosition < _logicalPosition)
        {
            throw new ArchiveOperationException(
                "Cannot seek backwards after FreezeAndReleaseBuffer() has been called."
            );
        }
#endif

        if (_recordingStartPosition is not null)
        {
            if (targetPosition >= _recordingStartPosition.Value && targetPosition <= streamPosition)
            {
                _logicalPosition = targetPosition;
                return true;
            }

            return false;
        }

        if (_ringBuffer is not null)
        {
            long ringBufferStart = streamPosition - _ringBuffer.Length;
            if (targetPosition >= ringBufferStart && targetPosition <= streamPosition)
            {
                _logicalPosition = targetPosition;
                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// Attempts to advance without reading and discarding bytes.
    /// Ring-buffered streams wrap non-seekable sources, so they cannot skip this way.
    /// </summary>
    internal virtual bool TrySkipForward(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        // Some format factories intentionally layer a detection buffer over an
        // existing wrapper (for example, Tar needs a larger BZip2 probe window).
        // Once FreezeAndReleaseBuffer has actually detached that ring, forward
        // skips can safely use the underlying stream's native strategy.
        if (_bufferReleaseRequested && _ringBuffer is null)
        {
            if (stream is SharpCompressStream sharpCompressStream)
            {
                return sharpCompressStream.TrySkipForward(count);
            }

            if (stream.CanSeek)
            {
                stream.Position += count;
                return true;
            }
        }

        return false;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        // If ring buffer exists, use unified buffered read logic
        if (_ringBuffer is not null)
        {
            return ReadWithRingBuffer(buffer, offset, count);
        }

        // No buffering - read directly from stream
        int read = stream.Read(buffer, offset, count);
        streamPosition += read;
        _logicalPosition = streamPosition;
        return read;
    }

    /// <summary>
    /// Reads data using the ring buffer. If logical position is behind stream position,
    /// serves data from the ring buffer first. Handles both recording mode and
    /// over-read protection uniformly. When buffer release was requested, frees the ring
    /// once the logical cursor catches up and then reads directly from the underlying stream.
    /// </summary>
    private int ReadWithRingBuffer(byte[] buffer, int offset, int count) =>
        ReadWithRingBuffer(buffer.AsSpan(offset, count));

    private int ReadWithRingBuffer(Span<byte> buffer)
    {
        int totalRead = 0;
        int offset = 0;
        int count = buffer.Length;

        // If logical position is behind stream position, read from ring buffer first
        while (count > 0 && _logicalPosition < streamPosition)
        {
            long bytesFromEnd = streamPosition - _logicalPosition;

            // Verify data is available in ring buffer
            if (!_ringBuffer!.CanReadFromEnd(bytesFromEnd))
            {
                throw new ArchiveOperationException(
                    $"Ring buffer underflow: trying to read {bytesFromEnd} bytes back, "
                        + $"but buffer only holds {_ringBuffer.Length} bytes."
                );
            }

            int available = _ringBuffer.ReadFromEnd(bytesFromEnd, buffer.Slice(offset, count));
            totalRead += available;
            offset += available;
            count -= available;
            _logicalPosition += available;
        }

        // Caught up: free the ring if release was requested (stops further memcpy).
        TryReleaseRingBuffer();

        // If more data needed and we're caught up, read from underlying stream
        if (count > 0 && _logicalPosition == streamPosition)
        {
            int read = stream.Read(buffer.Slice(offset, count));
            if (read > 0)
            {
                // Only write through the ring while it is still allocated.
                _ringBuffer?.Write(buffer.Slice(offset, read));
                streamPosition += read;
                _logicalPosition += read;
                totalRead += read;
            }
        }

        return totalRead;
    }

    /// <summary>
    /// Disposes the ring buffer when release was requested and all replayed bytes have been consumed.
    /// </summary>
    private void TryReleaseRingBuffer()
    {
        if (
            _bufferReleaseRequested
            && _logicalPosition == streamPosition
            && _ringBuffer is not null
        )
        {
            _ringBuffer.Dispose();
            _ringBuffer = null;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long targetPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => throw new NotSupportedException("Seeking from end is not supported."),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        SeekToPosition(targetPosition);
        return targetPosition;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_ringBuffer is not null)
        {
            return ReadWithRingBuffer(buffer);
        }

        int read = stream.Read(buffer);
        streamPosition += read;
        _logicalPosition = streamPosition;
        return read;
    }

    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
}
