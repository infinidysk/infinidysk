using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Compressors.Rar;

/// <summary>
/// Direct positional stream over stored (method 0) RAR entry parts.
/// Avoids the unpacker / <see cref="RarStream"/> copy path for seekable non-encrypted,
/// non-solid stored entries. RAR5 encrypted stored entries use
/// <see cref="EncryptedStoredRarEntryStream"/> instead.
/// </summary>
internal sealed partial class StoredRarEntryStream : Stream
{
    private readonly SeekableFilePart[] _parts;
    private readonly long[] _cumOffsets; // length = parts.Length + 1
    private readonly long _length;
    private readonly byte[]? _expectedCrc;
    private long _position;
    private uint _currentCrc = 0xffffffff;
    private bool _crcDisabled;
    private bool _disposed;

    private StoredRarEntryStream(SeekableFilePart[] parts, long length, byte[]? expectedCrc)
    {
        _parts = parts;
        _length = length;
        _expectedCrc = expectedCrc;
        _cumOffsets = new long[parts.Length + 1];
        for (var i = 0; i < parts.Length; i++)
        {
            _cumOffsets[i + 1] = _cumOffsets[i] + parts[i].FileHeader.CompressedSize;
        }

        // Null FileCrc disables validation (same practical outcome as encrypted disableCRC).
        if (_expectedCrc is null || _expectedCrc.Length != 4)
        {
            _crcDisabled = true;
        }
    }

    internal static bool TryCreate(
        ICollection<RarFilePart> parts,
        [NotNullWhen(true)] out StoredRarEntryStream? stream
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
            || first.IsEncrypted
            || first.IsSolid
            || first.IsRedir
            || first.FileCrc?.Length > 5
        )
        {
            return false;
        }

        // Expected CRC lives on the final (non-split-after) part for multi-volume entries.
        byte[]? expectedCrc = null;
        for (var p = seekable.Length - 1; p >= 0; p--)
        {
            if (!seekable[p].FileHeader.IsSplitAfter)
            {
                expectedCrc = seekable[p].FileHeader.FileCrc;
                break;
            }
        }

        stream = new StoredRarEntryStream(seekable, first.UncompressedSize, expectedCrc);
        return true;
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

        // Any Seek disables CRC for this stream instance (HTTP range / seek use cases).
        _crcDisabled = true;
        _currentCrc = 0xffffffff;
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
            ValidateCrcAtEof(requestedCount: buffer.Length);
            return 0;
        }

        var totalRead = 0;
        while (totalRead < buffer.Length && _position < _length)
        {
            var partIndex = FindPartIndex(_position);
            var part = _parts[partIndex];
            var intraPart = _position - _cumOffsets[partIndex];
            var remainingInPart = part.FileHeader.CompressedSize - intraPart;
            var remainingInEntry = _length - _position;
            var toRead = (int)
                Math.Min(buffer.Length - totalRead, Math.Min(remainingInPart, remainingInEntry));

            var volume = part.VolumeStream;
            // Re-seek every read: parts on a volume share one Stream.Position.
            volume.Position = part.FileHeader.DataStartPosition + intraPart;
            var read = volume.Read(buffer.Slice(totalRead, toRead));
            if (read == 0)
            {
                throw new IncompleteArchiveException(
                    "Unexpected end of volume while reading stored RAR entry data."
                );
            }

            if (!_crcDisabled)
            {
                _currentCrc = RarCRC.CheckCrc(_currentCrc, buffer.Slice(totalRead, read), 0, read);
            }

            _position += read;
            totalRead += read;
        }

        if (totalRead == 0)
        {
            ValidateCrcAtEof(requestedCount: buffer.Length);
        }

        return totalRead;
    }

    private void ValidateCrcAtEof(int requestedCount)
    {
        if (
            !_crcDisabled
            && requestedCount != 0
            && _expectedCrc is not null
            && GetCrc() != BitConverter.ToUInt32(_expectedCrc, 0)
        )
        {
            throw new InvalidFormatException("file crc mismatch");
        }
    }

    private uint GetCrc() => ~_currentCrc;

    private int FindPartIndex(long position)
    {
        // Binary search: find largest i where _cumOffsets[i] <= position < _cumOffsets[i+1]
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

        // Position may equal Length (handled before call) or fall in last empty range.
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
        _disposed = true;
        // Volume streams are owned by the archive; do not dispose them.
        base.Dispose(disposing);
    }
}
