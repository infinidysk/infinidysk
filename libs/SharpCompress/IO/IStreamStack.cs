using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SharpCompress.Common;

namespace SharpCompress.IO;

public interface IStreamStack
{
    /// <summary>
    /// Returns the immediate underlying stream in the stack.
    /// </summary>
    Stream BaseStream();
}

public static class StreamStackExtensions
{
    public static T? GetStream<T>(this IStreamStack stack)
        where T : Stream
    {
        var baseStream = stack.BaseStream();
        if (baseStream is T tStream)
        {
            return tStream;
        }
        else if (baseStream is IStreamStack innerStack)
        {
            return innerStack.GetStream<T>();
        }
        else
        {
            return null;
        }
    }

    /// <summary>
    /// Walks the stream stack and returns the first stream implementing <typeparamref name="T"/>.
    /// Mirrors <see cref="GetStream{T}"/> but matches an interface rather than a concrete
    /// <see cref="Stream"/> type (which is why a separate helper is needed: <see cref="GetStream{T}"/>
    /// constrains <c>T : Stream</c>).
    /// </summary>
    internal static T? FindImplementing<T>(this IStreamStack stack)
        where T : class
    {
        var baseStream = stack.BaseStream();
        if (baseStream is T match)
        {
            return match;
        }
        else if (baseStream is IStreamStack innerStack)
        {
            return innerStack.FindImplementing<T>();
        }
        else
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the root underlying stream at the bottom of the stack.
    /// This is useful for seeking when the intermediate streams don't support it.
    /// </summary>
    public static Stream GetRootStream(this IStreamStack stack)
    {
        var current = stack.BaseStream();
        while (current is IStreamStack streamStack)
        {
            current = streamStack.BaseStream();
        }
        return current;
    }

    /// <summary>
    /// Attempts to rewind <paramref name="count"/> bytes within a buffered
    /// <see cref="SharpCompressStream"/> in the stack.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the rewind succeeded, <paramref name="count"/> is zero, or there is
    /// no buffered <see cref="SharpCompressStream"/> to rewind into;
    /// <see langword="false"/> if a buffered stream was found but the target lies outside its valid range.
    /// </returns>
    internal static bool Rewind(this IStreamStack stream, int count)
    {
        if (count == 0)
        {
            return true;
        }

        IStreamStack? current = stream;

        while (current != null)
        {
            if (current is SharpCompressStream sharpCompressStream)
            {
                var targetPosition = sharpCompressStream.Position - count;
                if (targetPosition < 0)
                {
                    return false;
                }

                return sharpCompressStream.TrySetBufferedPosition(targetPosition);
            }

            current = current.BaseStream() as IStreamStack;
        }

        // No buffered SharpCompressStream in the stack — nothing to rewind into.
        return true;
    }

    /// <summary>
    /// Rewinds <paramref name="count"/> bytes or throws when the buffered region cannot cover the rewind.
    /// </summary>
    internal static void RewindOrThrow(this IStreamStack stream, int count)
    {
        if (!stream.Rewind(count))
        {
            throw new ArchiveOperationException(
                $"Unable to rewind {count} bytes within the stream buffer. "
                    + $"Increase {nameof(Constants)}.{nameof(Constants.RewindableBufferSize)} "
                    + $"(currently {Constants.RewindableBufferSize})."
            );
        }
    }
}
