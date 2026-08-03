using System;
using System.IO;
using SharpCompress.Common;

namespace SharpCompress.IO;

public partial class SharpCompressStream
{
    /// <summary>
    /// Creates a <see cref="SharpCompressStream"/> that acts as a zero-overhead passthrough wrapper
    /// around <paramref name="stream"/> without taking ownership of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a thin wrapper: all reads, writes, and seeks are forwarded directly to the underlying
    /// stream with no ring-buffer overhead. <see cref="Stream.CanSeek"/> delegates to the underlying
    /// stream's own value. <see cref="Stream.Position"/> likewise reports and sets the underlying
    /// stream position.
    /// </para>
    /// <para>
    /// The resulting stream does <b>not</b> support <see cref="StartRecording"/>, <see cref="Rewind()"/>,
    /// <see cref="StopRecording"/>, or <see cref="FreezeAndReleaseBuffer"/> (each throws
    /// <see cref="ArchiveOperationException"/>). Call
    /// <see cref="Create(Stream, int?)"/> on the passthrough stream to obtain a recording-capable
    /// wrapper when needed (that call unwraps this passthrough and rewraps the underlying stream).
    /// </para>
    /// <para>
    /// Because the stream does not take ownership, the underlying stream is <b>never</b> disposed when
    /// this wrapper is disposed (<see cref="LeaveStreamOpen"/> is always <see langword="true"/>).
    /// Use this when you need to satisfy an API that expects a <see cref="SharpCompressStream"/>
    /// without transferring lifetime responsibility.
    /// </para>
    /// </remarks>
    /// <param name="stream">The underlying stream to wrap. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// A passthrough <see cref="SharpCompressStream"/> that does not dispose <paramref name="stream"/>.
    /// </returns>
    public static SharpCompressStream CreateNonDisposing(Stream stream) =>
        new PassthroughSharpCompressStream(stream);

    /// <summary>
    /// Creates a <see cref="SharpCompressStream"/> that supports recording and rewinding over
    /// <paramref name="stream"/>, choosing the most efficient strategy based on the stream's
    /// capabilities.
    /// </summary>
    /// <remarks>
    /// <para><b>Seekable streams</b> — wraps in a thin delegate (<c>SeekableSharpCompressStream</c>)
    /// that calls the underlying stream's native <see cref="Stream.Seek"/> directly. No ring buffer
    /// is allocated. <see cref="StartRecording"/> stores the current position; <see cref="Rewind()"/>
    /// seeks back to it. <paramref name="bufferSize"/> is ignored.</para>
    /// <para><b>Non-seekable streams</b> (network streams, compressed streams, pipes) — allocates
    /// a ring buffer of <paramref name="bufferSize"/> bytes. All bytes read from the underlying
    /// stream are kept in the ring buffer so that <see cref="Rewind()"/> can replay them without
    /// re-reading the underlying stream. If more bytes have been read than the ring buffer can hold,
    /// a subsequent rewind will throw <see cref="ArchiveOperationException"/>; increase
    /// <paramref name="bufferSize"/> or <see cref="Common.Constants.RewindableBufferSize"/> to
    /// avoid this.</para>
    /// <para><b>Already-wrapped streams — unwrap / return-as-is rules:</b></para>
    /// <list type="bullet">
    /// <item>
    /// Passthrough <see cref="SharpCompressStream"/> (<c>PassthroughSharpCompressStream</c>):
    /// unwrapped and rewrapped. Seekable underlying streams become
    /// <c>SeekableSharpCompressStream</c>; non-seekable underlying streams become a
    /// ring-buffered <see cref="SharpCompressStream"/>. In both cases
    /// <see cref="LeaveStreamOpen"/> is <see langword="true"/> so prior
    /// <see cref="CreateNonDisposing"/> ownership is preserved.
    /// <paramref name="bufferSize"/> applies only to the non-seekable rewrap path.
    /// </item>
    /// <item>
    /// Non-passthrough <see cref="SharpCompressStream"/>: returned as-is.
    /// <paramref name="bufferSize"/> is ignored (no reallocation / no double-wrap).
    /// </item>
    /// <item>
    /// <see cref="IStreamStack"/> whose stack contains a <see cref="SharpCompressStream"/>: that
    /// inner instance is returned as-is. <paramref name="bufferSize"/> is ignored.
    /// </item>
    /// </list>
    /// <para>
    /// Ownership for newly created wrappers over raw (not already wrapped) streams:
    /// <see cref="LeaveStreamOpen"/> is <see langword="false"/> — disposing the returned instance
    /// disposes <paramref name="stream"/>.
    /// </para>
    /// </remarks>
    /// <param name="stream">The underlying stream to wrap. Must not be <see langword="null"/>.</param>
    /// <param name="bufferSize">
    /// Size in bytes of the ring buffer allocated for non-seekable streams.
    /// Defaults to <see cref="Common.Constants.RewindableBufferSize"/> (81 920 bytes) when
    /// <see langword="null"/>. Silently ignored when <paramref name="stream"/> is seekable, or when
    /// an existing non-passthrough / stacked <see cref="SharpCompressStream"/> is returned as-is.
    /// </param>
    /// <returns>
    /// A <see cref="SharpCompressStream"/> wrapping <paramref name="stream"/>. Newly created wrappers
    /// over raw streams own the stream and will dispose it; wrappers produced by unwrapping a
    /// <see cref="CreateNonDisposing"/> passthrough do not.
    /// </returns>
    public static SharpCompressStream Create(Stream stream, int? bufferSize = null)
    {
        var rewindableBufferSize = bufferSize ?? Constants.RewindableBufferSize;

        // Unwrap non-disposing passthroughs. If the underlying stream is already a
        // SharpCompressStream, return that concrete buffering strategy as-is. In
        // particular, a ring-buffered stream reports CanSeek for replay within its
        // buffered window, but it must never be treated as natively seekable.
        if (stream is SharpCompressStream sharpCompressStream)
        {
            while (sharpCompressStream is PassthroughSharpCompressStream)
            {
                var underlying = sharpCompressStream.stream;
                if (underlying is SharpCompressStream underlyingSharpCompressStream)
                {
                    sharpCompressStream = underlyingSharpCompressStream;
                    continue;
                }

                if (underlying.CanSeek)
                {
                    return new SeekableSharpCompressStream(underlying, true);
                }

                return new SharpCompressStream(underlying, true, rewindableBufferSize);
            }

            return sharpCompressStream;
        }

        // Check if stream is wrapping a SharpCompressStream (e.g., via IStreamStack)
        if (stream is IStreamStack streamStack)
        {
            var underlying = streamStack.GetStream<SharpCompressStream>();
            if (underlying is not null)
            {
                return underlying;
            }
        }

        if (stream.CanSeek)
        {
            return new SeekableSharpCompressStream(stream);
        }

        // For non-seekable streams, create a SharpCompressStream with rolling buffer
        // to allow limited backward seeking (required by decompressors that over-read)
        return new SharpCompressStream(stream, false, rewindableBufferSize);
    }
}
