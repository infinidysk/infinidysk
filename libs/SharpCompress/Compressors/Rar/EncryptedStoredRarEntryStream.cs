using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Compressors.Rar;

/// <summary>
/// Direct positional stream over encrypted stored (method 0) RAR5 entry parts.
/// Avoids the unpacker / <see cref="RarStream"/> copy path for seekable encrypted
/// stored entries. RAR3 encrypted and compressed encrypted entries stay on the slow path.
/// </summary>
internal sealed partial class EncryptedStoredRarEntryStream : Stream
{
    private const int AesBlockSize = 16;

    private readonly SeekableFilePart[] _parts;
    private readonly long[] _cumOffsets; // length = parts.Length + 1, plaintext byte offsets
    private readonly byte[] _aesKey;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    private EncryptedStoredRarEntryStream(
        SeekableFilePart[] parts,
        long[] cumOffsets,
        byte[] aesKey,
        long length
    )
    {
        _parts = parts;
        _cumOffsets = cumOffsets;
        _aesKey = aesKey;
        _length = length;
    }

    internal static bool TryCreate(
        ICollection<RarFilePart> parts,
        [NotNullWhen(true)] out EncryptedStoredRarEntryStream? stream
    )
    {
        stream = null;
        if (parts.Count == 0)
        {
            return false;
        }

        var seekable = new SeekableFilePart[parts.Count];
        var i = 0;
        foreach (var part in parts)
        {
            if (part is not SeekableFilePart sfp || !sfp.VolumeStream.CanSeek)
            {
                return false;
            }

            seekable[i++] = sfp;
        }

        var first = seekable[0].FileHeader;
        if (
            !first.IsStored
            || !first.IsEncrypted
            || first.Rar5CryptoInfo is null
            || first.R4Salt is not null
            || first.IsSolid
            || first.IsRedir
        )
        {
            return false;
        }

        byte[] aesKey;
        var cryptKey = new CryptKey5(seekable[0].Password, first.Rar5CryptoInfo);
        aesKey = cryptKey.GetAesKey(first.Rar5CryptoInfo.Salt);

        var cumOffsets = BuildPlaintextOffsets(seekable, first.UncompressedSize);
        if (cumOffsets is null)
        {
            return false;
        }

        stream = new EncryptedStoredRarEntryStream(
            seekable,
            cumOffsets,
            aesKey,
            first.UncompressedSize
        );
        return true;
    }

    private static long[]? BuildPlaintextOffsets(SeekableFilePart[] parts, long totalPlaintext)
    {
        var cumOffsets = new long[parts.Length + 1];
        long assigned = 0;

        for (var i = 0; i < parts.Length; i++)
        {
            cumOffsets[i] = assigned;
            var header = parts[i].FileHeader;
            if (header.Rar5CryptoInfo is null)
            {
                return null;
            }

            long plainPartSize;
            if (header.IsSplitAfter)
            {
                // Volume splits align to AES blocks; ciphertext is an exact multiple of 16.
                plainPartSize = header.CompressedSize;
            }
            else
            {
                plainPartSize = totalPlaintext - assigned;
            }

            if (plainPartSize < 0 || assigned + plainPartSize > totalPlaintext)
            {
                return null;
            }

            assigned += plainPartSize;
        }

        cumOffsets[parts.Length] = assigned;
        return assigned == totalPlaintext ? cumOffsets : null;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (newPos < 0)
        {
            throw new IOException(
                "An attempt was made to move the position before the beginning of the stream."
            );
        }

        _position = newPos;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);
        return ReadCore(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer) => ReadCore(buffer);

    private int ReadCore(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
        {
            return 0;
        }

        if (_position >= _length)
        {
            return 0;
        }

        var totalRead = 0;
        while (totalRead < buffer.Length && _position < _length)
        {
            var partIndex = FindPartIndex(_position);
            var part = _parts[partIndex];
            var intraPart = _position - _cumOffsets[partIndex];
            var remainingInPart = _cumOffsets[partIndex + 1] - _position;
            var remainingInEntry = _length - _position;
            var toRead = (int)
                Math.Min(buffer.Length - totalRead, Math.Min(remainingInPart, remainingInEntry));

            DecryptPlaintext(part, intraPart, buffer.Slice(totalRead, toRead));
            _position += toRead;
            totalRead += toRead;
        }

        return totalRead;
    }

    private void DecryptPlaintext(
        SeekableFilePart part,
        long plainOffsetInPart,
        Span<byte> destination
    )
    {
        var crypto = part.FileHeader.Rar5CryptoInfo!;
        var blockIndex = plainOffsetInPart / AesBlockSize;
        var offsetInBlock = (int)(plainOffsetInPart % AesBlockSize);
        var dataStart = part.FileHeader.DataStartPosition;
        var volume = part.VolumeStream;

        var cipherBlock = ArrayPool<byte>.Shared.Rent(AesBlockSize);
        var plainBlock = ArrayPool<byte>.Shared.Rent(AesBlockSize);
        var ivBlock = ArrayPool<byte>.Shared.Rent(AesBlockSize);
        try
        {
            var destOffset = 0;
            while (destOffset < destination.Length)
            {
                if (blockIndex == 0)
                {
                    crypto.InitV.CopyTo(ivBlock, 0);
                }
                else
                {
                    ReadCipherBlock(volume, dataStart, blockIndex - 1, ivBlock);
                }

                ReadCipherBlock(volume, dataStart, blockIndex, cipherBlock);
                using (var aes = Aes.Create())
                {
                    aes.Key = _aesKey;
                    aes.DecryptCbc(
                        cipherBlock.AsSpan(0, AesBlockSize),
                        ivBlock.AsSpan(0, AesBlockSize),
                        plainBlock.AsSpan(0, AesBlockSize),
                        PaddingMode.None
                    );
                }

                var available = AesBlockSize - offsetInBlock;
                var toCopy = Math.Min(available, destination.Length - destOffset);
                plainBlock.AsSpan(offsetInBlock, toCopy).CopyTo(destination.Slice(destOffset));
                destOffset += toCopy;
                offsetInBlock = 0;
                blockIndex++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cipherBlock, clearArray: true);
            ArrayPool<byte>.Shared.Return(plainBlock, clearArray: true);
            ArrayPool<byte>.Shared.Return(ivBlock, clearArray: true);
        }
    }

    private static void ReadCipherBlock(
        Stream volume,
        long dataStart,
        long blockIndex,
        byte[] buffer
    )
    {
        volume.Position = dataStart + blockIndex * AesBlockSize;
        volume.ReadExactly(buffer.AsSpan(0, AesBlockSize));
    }

    private int FindPartIndex(long position)
    {
        var lo = 0;
        var hi = _parts.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (position < _cumOffsets[mid])
            {
                hi = mid - 1;
            }
            else if (position >= _cumOffsets[mid + 1])
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return Math.Clamp(lo, 0, _parts.Length - 1);
    }

    private static void ValidateBufferArgs(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("Offset and length were out of bounds for the array.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CryptographicOperations.ZeroMemory(_aesKey);
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
