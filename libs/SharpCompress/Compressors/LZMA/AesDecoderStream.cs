using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA.Utilities;

namespace SharpCompress.Compressors.LZMA;

internal sealed partial class AesDecoderStream : DecoderStream2
{
    // 4 KB, multiple of the 16-byte AES block; small because 7z decrypt runs interleaved with decode.
    private const int DecryptBlockSize = 4 << 10;

    private readonly Stream mStream;
    private readonly ICryptoTransform mDecoder;
    private readonly byte[] mBuffer;
    private long mWritten;
    private readonly long mLimit;
    private int mOffset;
    private int mEnding;
    private int mUnderflow;
    private bool isDisposed;

    public AesDecoderStream(Stream input, byte[] info, IPasswordProvider pass, long limit)
    {
        var password = pass.CryptoGetTextPassword();
        if (password == null)
        {
            throw new SharpCompress.Common.CryptographicException(
                "Encrypted 7Zip archive has no password specified."
            );
        }

        mStream = input;
        mLimit = limit;

        if (((uint)input.Length & 15) != 0)
        {
            throw new NotSupportedException("AES decoder does not support padding.");
        }

        Init(info, out var numCyclesPower, out var salt, out var seed);

        var key = Aes7zKeyCache.DeriveKey(password, numCyclesPower, salt);

        using (var aes = Aes.Create())
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            mDecoder = aes.CreateDecryptor(key, seed);
        }

        mBuffer = new byte[DecryptBlockSize];
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (isDisposed)
            {
                return;
            }
            isDisposed = true;
            if (disposing)
            {
                mStream.Dispose();
                mDecoder.Dispose();
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    public override long Position => mWritten;

    public override long Length => mLimit;

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || mWritten == mLimit)
        {
            return 0;
        }

        if (mUnderflow > 0)
        {
            return HandleUnderflow(buffer);
        }

        if (mEnding - mOffset < 16)
        {
            Buffer.BlockCopy(mBuffer, mOffset, mBuffer, 0, mEnding - mOffset);
            mEnding -= mOffset;
            mOffset = 0;

            do
            {
                var read = mStream.Read(mBuffer.AsSpan(mEnding, mBuffer.Length - mEnding));
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
            return HandleUnderflow(buffer.Slice(0, count));
        }

        if (count > mEnding - mOffset)
        {
            count = mEnding - mOffset;
        }

        var processed = TransformBlockToSpan(mBuffer, mOffset, count & ~15, buffer);
        mOffset += processed;
        mWritten += processed;
        return processed;
    }

    #region Private Methods

    private void Init(byte[] info, out int numCyclesPower, out byte[] salt, out byte[] iv)
    {
        var bt = info[0];
        numCyclesPower = bt & 0x3F;

        if ((bt & 0xC0) == 0)
        {
            salt = Array.Empty<byte>();
            iv = Array.Empty<byte>();
            return;
        }

        var saltSize = (bt >> 7) & 1;
        var ivSize = (bt >> 6) & 1;
        if (info.Length == 1)
        {
            throw new ArchiveOperationException();
        }

        var bt2 = info[1];
        saltSize += (bt2 >> 4);
        ivSize += (bt2 & 15);
        if (info.Length < 2 + saltSize + ivSize)
        {
            throw new ArchiveOperationException();
        }

        salt = new byte[saltSize];
        for (var i = 0; i < saltSize; i++)
        {
            salt[i] = info[i + 2];
        }

        iv = new byte[16];
        for (var i = 0; i < ivSize; i++)
        {
            iv[i] = info[i + saltSize + 2];
        }

        if (numCyclesPower > 24)
        {
            throw new NotSupportedException();
        }
    }

    private int HandleUnderflow(byte[] buffer, int offset, int count) =>
        HandleUnderflow(buffer.AsSpan(offset, count));

    private int HandleUnderflow(Span<byte> buffer)
    {
        if (mUnderflow == 0)
        {
            var blockSize = (mEnding - mOffset) & ~15;
            mUnderflow = mDecoder.TransformBlock(mBuffer, mOffset, blockSize, mBuffer, mOffset);
        }

        var count = buffer.Length;
        if (count > mUnderflow)
        {
            count = mUnderflow;
        }

        mBuffer.AsSpan(mOffset, count).CopyTo(buffer);
        mWritten += count;
        mOffset += count;
        mUnderflow -= count;
        return count;
    }

    private int TransformBlockToSpan(
        byte[] input,
        int inputOffset,
        int inputCount,
        Span<byte> destination
    )
    {
        var temp = ArrayPool<byte>.Shared.Rent(inputCount);
        try
        {
            var processed = mDecoder.TransformBlock(input, inputOffset, inputCount, temp, 0);
            temp.AsSpan(0, processed).CopyTo(destination);
            return processed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    #endregion
}
