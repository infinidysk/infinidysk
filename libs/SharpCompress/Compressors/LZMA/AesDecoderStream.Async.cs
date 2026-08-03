using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;

namespace SharpCompress.Compressors.LZMA;

internal sealed partial class AesDecoderStream
{
    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken = default
    ) => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (buffer.Length == 0 || mWritten == mLimit)
        {
            return 0;
        }

        if (mUnderflow > 0)
        {
            return HandleUnderflow(buffer.Span);
        }

        if (mEnding - mOffset < 16)
        {
            Buffer.BlockCopy(mBuffer, mOffset, mBuffer, 0, mEnding - mOffset);
            mEnding -= mOffset;
            mOffset = 0;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await mStream
                    .ReadAsync(
                        mBuffer.AsMemory(mEnding, mBuffer.Length - mEnding),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IncompleteArchiveException("Unexpected end of stream.");
                }

                mEnding += read;
            } while (mEnding - mOffset < 16);
        }

        var count = buffer.Length;
        if (count > mLimit - mWritten)
        {
            count = (int)(mLimit - mWritten);
        }

        if (count < 16)
        {
            return HandleUnderflow(buffer.Span.Slice(0, count));
        }

        if (count > mEnding - mOffset)
        {
            count = mEnding - mOffset;
        }

        var processed = TransformBlockToSpan(mBuffer, mOffset, count & ~15, buffer.Span);
        mOffset += processed;
        mWritten += processed;
        return processed;
    }
}
