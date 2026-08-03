using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SharpCompress.Common.Zip;

internal partial class WinzipAesCryptoStream : Stream
{
    private const int BLOCK_SIZE_IN_BYTES = 16;

    // Batch keystream generation to amortize EncryptEcb one-shot setup cost.
    private const int KEYSTREAM_BLOCKS = 32;
    private const int KEYSTREAM_SIZE_IN_BYTES = BLOCK_SIZE_IN_BYTES * KEYSTREAM_BLOCKS;

    private readonly Aes _cipher;
    private readonly byte[] _counter = new byte[KEYSTREAM_SIZE_IN_BYTES];
    private readonly Stream _stream;
    private int _nonce = 1;
    private readonly byte[] _counterOut = new byte[KEYSTREAM_SIZE_IN_BYTES];
    private int _counterOutOffset = KEYSTREAM_SIZE_IN_BYTES;
    private long _totalBytesLeftToRead;
    private bool _isDisposed;

    internal WinzipAesCryptoStream(
        Stream stream,
        WinzipAesEncryptionData winzipAesEncryptionData,
        long length
    )
    {
        _stream = stream;
        _totalBytesLeftToRead = length;
        _cipher = CreateCipher(winzipAesEncryptionData);
    }

    // WinZip AES uses CTR with a little-endian counter. .NET has no CipherMode.CTR, so we
    // encrypt successive counter blocks with the raw AES block operation (EncryptEcb) to
    // produce keystream only — payload bytes are never ECB-encrypted.
    private static Aes CreateCipher(WinzipAesEncryptionData winzipAesEncryptionData)
    {
        var cipher = Aes.Create();
        cipher.Padding = PaddingMode.None;
        cipher.Key = winzipAesEncryptionData.KeyBytes;
        return cipher;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            base.Dispose(disposing);
            return;
        }
        _isDisposed = true;
        if (disposing)
        {
            // Read out last 10 auth bytes
            Span<byte> ten = stackalloc byte[10];
            _stream.ReadAtLeast(ten, ten.Length, throwOnEndOfStream: false);
            _stream.Dispose();
            _cipher.Dispose();
        }
        base.Dispose(disposing);
    }

    private async ValueTask ReadAuthBytesAsync()
    {
        byte[] authBytes = new byte[10];
        await _stream
            .ReadAtLeastAsync(authBytes.AsMemory(0, 10), 10, throwOnEndOfStream: false)
            .ConfigureAwait(false);
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_totalBytesLeftToRead == 0)
        {
            return 0;
        }
        var bytesToRead = count;
        if (count > _totalBytesLeftToRead)
        {
            bytesToRead = (int)_totalBytesLeftToRead;
        }
        var read = _stream.Read(buffer, offset, bytesToRead);
        _totalBytesLeftToRead -= read;

        ReadTransformBlocks(buffer, offset, read);

        return read;
    }

    private void FillCounterOut()
    {
        for (var i = 0; i < KEYSTREAM_BLOCKS; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                _counter.AsSpan(i * BLOCK_SIZE_IN_BYTES, sizeof(int)),
                _nonce++
            );
        }

        _cipher.EncryptEcb(_counter, _counterOut, PaddingMode.None);
        _counterOutOffset = 0;
    }

    private void XorInPlace(byte[] buffer, int offset, int count, int counterOffset)
    {
        for (var i = 0; i < count; i++)
        {
            buffer[offset + i] = (byte)(_counterOut[counterOffset + i] ^ buffer[offset + i]);
        }
    }

    private void ReadTransformBlocks(byte[] buffer, int offset, int count)
    {
        var posn = offset;
        var remaining = count;

        while (posn < buffer.Length && remaining > 0)
        {
            if (_counterOutOffset == KEYSTREAM_SIZE_IN_BYTES)
            {
                FillCounterOut();
            }

            var bytesToXor = Math.Min(KEYSTREAM_SIZE_IN_BYTES - _counterOutOffset, remaining);
            XorInPlace(buffer, posn, bytesToXor, _counterOutOffset);
            _counterOutOffset += bytesToXor;
            posn += bytesToXor;
            remaining -= bytesToXor;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
