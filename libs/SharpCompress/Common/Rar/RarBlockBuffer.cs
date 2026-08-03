using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Compressors.Rar;
using SharpCompress.Crypto;

namespace SharpCompress.Common.Rar;

/// <summary>
/// A pooled, in-memory view of a single RAR header block. The block is filled from the
/// stream once (optionally decrypting it), after which all field decoding happens
/// synchronously over the buffer. This lets the RAR header parsers share a single,
/// IO-free implementation instead of maintaining mirrored sync/async binary readers.
///
/// The read primitives, CRC accumulation, and <see cref="Mark"/>/<see cref="CurrentReadByteCount"/>
/// semantics match the historical MarkingBinaryReader/RarCrcBinaryReader so the existing
/// header parsing logic and CRC boundaries are preserved exactly.
/// </summary>
internal sealed class RarBlockBuffer : IDisposable
{
    // Enough bytes to observe the RAR4 fixed header size field (offset 5, 2 bytes) and the
    // RAR5 CRC (4 bytes) plus its size vint (at most 3 bytes). Reading this many bytes never
    // overruns a valid header because the smallest RAR4/RAR5 header is at least this large.
    private const int PrefixLength = 7;

    private readonly Stream _baseStream;
    private byte[] _data;
    private readonly int _length;
    private int _position;
    private int _markPosition;
    private uint _currentCrc;

    private RarBlockBuffer(Stream baseStream, byte[] data, int length)
    {
        _baseStream = baseStream;
        _data = data;
        _length = length;
    }

    // Test hook: wraps a raw byte sequence as a header block so the span read primitives,
    // CRC accumulation, and Mark()/CurrentReadByteCount semantics can be exercised directly.
    internal static RarBlockBuffer CreateForTest(byte[] data)
    {
        var rented = ArrayPool<byte>.Shared.Rent(data.Length == 0 ? 1 : data.Length);
        data.CopyTo(rented.AsSpan());
        return new RarBlockBuffer(Stream.Null, rented, data.Length);
    }

    public Stream BaseStream => _baseStream;

    // Bytes consumed since the last Mark(); mirrors the previous readers, which reset this
    // counter at Mark() so RemainingHeaderBytes = HeaderSize - CurrentReadByteCount is exact.
    public long CurrentReadByteCount => _position - _markPosition;

    public void Mark() => _markPosition = _position;

    public void ResetCrc() => _currentCrc = 0xffffffff;

    public uint GetCrc32() => ~_currentCrc;

    public byte ReadByte()
    {
        EnsureAvailable(1);
        var b = _data[_position++];
        _currentCrc = RarCRC.CheckCrc(_currentCrc, b);
        return b;
    }

    public bool ReadBoolean() => ReadByte() != 0;

    public byte[] ReadBytes(int count)
    {
        EnsureAvailable(count);
        var bytes = _data.AsSpan(_position, count).ToArray();
        _position += count;
        _currentCrc = RarCRC.CheckCrc(_currentCrc, bytes, 0, count);
        return bytes;
    }

    private ReadOnlySpan<byte> ReadSpan(int count)
    {
        EnsureAvailable(count);
        var span = _data.AsSpan(_position, count);
        _position += count;
        _currentCrc = RarCRC.CheckCrc(_currentCrc, span, 0, count);
        return span;
    }

    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(ReadSpan(2));

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadSpan(2));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadSpan(4));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadSpan(4));

    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadSpan(8));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadSpan(8));

    // RAR5 style variable length encoded value; see the technote referenced by RarHeader.
    public ulong ReadRarVInt(int maxBytes = 10) => DoReadRarVInt((maxBytes - 1) * 7);

    public uint ReadRarVIntUInt32(int maxBytes = 5) => DoReadRarVIntUInt32((maxBytes - 1) * 7);

    public ushort ReadRarVIntUInt16(int maxBytes = 3) =>
        checked((ushort)DoReadRarVIntUInt32((maxBytes - 1) * 7));

    public byte ReadRarVIntByte(int maxBytes = 2) =>
        checked((byte)DoReadRarVIntUInt32((maxBytes - 1) * 7));

    private ulong DoReadRarVInt(int maxShift)
    {
        var shift = 0;
        ulong result = 0;
        do
        {
            var b0 = ReadByte();
            var b1 = ((uint)b0) & 0x7f;
            ulong n = b1;
            var shifted = n << shift;
            if (n != shifted >> shift)
            {
                break;
            }
            result |= shifted;
            if (b0 == b1)
            {
                return result;
            }
            shift += 7;
        } while (shift <= maxShift);

        throw new InvalidFormatException("malformed vint");
    }

    private uint DoReadRarVIntUInt32(int maxShift)
    {
        var shift = 0;
        uint result = 0;
        do
        {
            var b0 = ReadByte();
            var b1 = ((uint)b0) & 0x7f;
            var n = b1;
            var shifted = n << shift;
            if (n != shifted >> shift)
            {
                break;
            }
            result |= shifted;
            if (b0 == b1)
            {
                return result;
            }
            shift += 7;
        } while (shift <= maxShift);

        throw new InvalidFormatException("malformed vint");
    }

    private void EnsureAvailable(int count)
    {
        if (count < 0 || _position + count > _length)
        {
            throw new InvalidFormatException(
                string.Format(
                    Constants.DefaultCultureInfo,
                    "Could not read the requested amount of bytes. End of header reached. Requested: {0} Remaining: {1}",
                    count,
                    _length - _position
                )
            );
        }
    }

    /// <summary>
    /// Reads a single RAR header block from the stream synchronously. When
    /// <paramref name="decryptor"/> is non-null the block is decrypted as it is read.
    /// </summary>
    public static RarBlockBuffer ReadHeaderBlock(
        Stream stream,
        bool isRar5,
        RarAesDecryptor? decryptor
    )
    {
        var prefix = new byte[PrefixLength];
        FillSync(stream, decryptor, prefix);
        var totalLength = DecodeTotalLength(prefix, isRar5);

        var data = ArrayPool<byte>.Shared.Rent(totalLength);
        prefix.CopyTo(data.AsSpan());
        if (totalLength > PrefixLength)
        {
            FillSync(stream, decryptor, data.AsSpan(PrefixLength, totalLength - PrefixLength));
        }
        return new RarBlockBuffer(stream, data, totalLength);
    }

    /// <summary>
    /// Asynchronous twin of <see cref="ReadHeaderBlock"/>. Only the block-fill I/O differs;
    /// all subsequent field decoding is identical (and synchronous).
    /// </summary>
    public static async ValueTask<RarBlockBuffer> ReadHeaderBlockAsync(
        Stream stream,
        bool isRar5,
        RarAesDecryptor? decryptor,
        CancellationToken cancellationToken
    )
    {
        var prefix = new byte[PrefixLength];
        await FillAsync(stream, decryptor, prefix, cancellationToken).ConfigureAwait(false);
        var totalLength = DecodeTotalLength(prefix, isRar5);

        var data = ArrayPool<byte>.Shared.Rent(totalLength);
        prefix.CopyTo(data.AsSpan());
        if (totalLength > PrefixLength)
        {
            await FillAsync(
                    stream,
                    decryptor,
                    data.AsMemory(PrefixLength, totalLength - PrefixLength),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        return new RarBlockBuffer(stream, data, totalLength);
    }

    private static void FillSync(Stream stream, RarAesDecryptor? decryptor, Span<byte> destination)
    {
        if (decryptor is null)
        {
            ReadExactlyOrThrow(stream, destination);
        }
        else
        {
            decryptor.Fill(stream, destination);
        }
    }

    private static async ValueTask FillAsync(
        Stream stream,
        RarAesDecryptor? decryptor,
        Memory<byte> destination,
        CancellationToken cancellationToken
    )
    {
        if (decryptor is null)
        {
            await ReadExactlyOrThrowAsync(stream, destination, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await decryptor.FillAsync(stream, destination, cancellationToken).ConfigureAwait(false);
        }
    }

    // Determines the total number of bytes that make up the header block, given the fixed
    // prefix already read. The result includes the prefix bytes themselves.
    private static int DecodeTotalLength(ReadOnlySpan<byte> prefix, bool isRar5)
    {
        int total;
        if (isRar5)
        {
            // 4-byte CRC, then a size vint whose value is the byte count that follows it.
            var pos = 4;
            uint size = 0;
            var shift = 0;
            while (true)
            {
                if (pos >= prefix.Length)
                {
                    throw new InvalidFormatException("malformed vint");
                }
                var b = prefix[pos++];
                size |= (uint)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                {
                    break;
                }
                shift += 7;
                // Matches ReadRarVIntUInt32(3): a header size vint is at most 3 bytes.
                if (shift > 14)
                {
                    throw new InvalidFormatException("malformed vint");
                }
            }
            total = pos + (int)size;
        }
        else
        {
            // RAR4: the header size field is a 2-byte value at offset 5 covering the whole
            // header block (including the leading 2-byte CRC).
            total = BinaryPrimitives.ReadInt16LittleEndian(prefix.Slice(5, 2));
        }

        if (total < PrefixLength)
        {
            throw new InvalidFormatException("rar header size too small");
        }
        return total;
    }

    /// <summary>
    /// Reads plaintext bytes directly from the stream (no CRC, no decryption). Used for the
    /// per-header salt/IV that precedes an encrypted RAR header block.
    /// </summary>
    public static byte[] ReadStreamBytes(Stream stream, int count)
    {
        var bytes = new byte[count];
        ReadExactlyOrThrow(stream, bytes);
        return bytes;
    }

    public static async ValueTask<byte[]> ReadStreamBytesAsync(
        Stream stream,
        int count,
        CancellationToken cancellationToken
    )
    {
        var bytes = new byte[count];
        await ReadExactlyOrThrowAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static void ReadExactlyOrThrow(Stream stream, Span<byte> buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException e)
        {
            throw new RarHeaderReadException(
                "Unexpected end of stream reading rar header.",
                truncated: true,
                e
            );
        }
    }

    private static async ValueTask ReadExactlyOrThrowAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            throw new RarHeaderReadException(
                "Unexpected end of stream reading rar header.",
                truncated: true,
                e
            );
        }
    }

    public void Dispose()
    {
        var data = _data;
        _data = [];
        if (data.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }
}
