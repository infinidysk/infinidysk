using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpCompress.Crypto;

/// <summary>
/// SHA-1 with copyable intermediate state so RAR3 KDF can finalize checkpoints
/// without cloning a large buffer (mirrors unrar's <c>sha1_context</c> copy).
/// </summary>
[SuppressMessage(
    "Security",
    "CA5350:Do Not Use Weak Cryptographic Algorithms",
    Justification = "RAR3 key derivation is SHA-1 based by format definition."
)]
internal struct CopyableSha1
{
    private uint _h0;
    private uint _h1;
    private uint _h2;
    private uint _h3;
    private uint _h4;
    private ulong _length;
    private Sha1Block _block;
    private int _blockLength;

    public static CopyableSha1 Create()
    {
        var sha = new CopyableSha1();
        sha.Initialize();
        return sha;
    }

    public void Initialize()
    {
        _h0 = 0x67452301;
        _h1 = 0xEFCDAB89;
        _h2 = 0x98BADCFE;
        _h3 = 0x10325476;
        _h4 = 0xC3D2E1F0;
        _length = 0;
        _blockLength = 0;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        _length += (ulong)data.Length;

        if (_blockLength > 0)
        {
            var need = 64 - _blockLength;
            if (data.Length < need)
            {
                data.CopyTo(BlockSpan(_blockLength));
                _blockLength += data.Length;
                return;
            }

            data[..need].CopyTo(BlockSpan(_blockLength));
            Transform(BlockSpan(0));
            _blockLength = 0;
            data = data[need..];
        }

        while (data.Length >= 64)
        {
            Transform(data[..64]);
            data = data[64..];
        }

        if (data.Length > 0)
        {
            data.CopyTo(BlockSpan(0));
            _blockLength = data.Length;
        }
    }

    /// <summary>
    /// Finalizes this instance into a 20-byte digest. Mutates state; copy first for checkpoints.
    /// </summary>
    public void FinalizeTo(Span<byte> digest20)
    {
        if (digest20.Length < 20)
        {
            throw new ArgumentException("SHA-1 digest requires 20 bytes.", nameof(digest20));
        }

        var bitLength = _length * 8;
        var block = BlockSpan(0);
        block[_blockLength++] = 0x80;

        if (_blockLength > 56)
        {
            block[_blockLength..].Clear();
            Transform(block);
            _blockLength = 0;
        }

        block[_blockLength..56].Clear();
        BinaryPrimitivesWriteUInt64BigEndian(block[56..], bitLength);
        Transform(block);
        _blockLength = 0;

        WriteUInt32BigEndian(digest20, _h0);
        WriteUInt32BigEndian(digest20[4..], _h1);
        WriteUInt32BigEndian(digest20[8..], _h2);
        WriteUInt32BigEndian(digest20[12..], _h3);
        WriteUInt32BigEndian(digest20[16..], _h4);
    }

    private Span<byte> BlockSpan(int offset) =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<Sha1Block, byte>(ref _block), 64)[offset..];

    private void Transform(ReadOnlySpan<byte> block)
    {
        Span<uint> w = stackalloc uint[80];
        for (var i = 0; i < 16; i++)
        {
            w[i] = BinaryPrimitivesReadUInt32BigEndian(block.Slice(i * 4, 4));
        }

        for (var i = 16; i < 80; i++)
        {
            w[i] = RotateLeft(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        }

        var a = _h0;
        var b = _h1;
        var c = _h2;
        var d = _h3;
        var e = _h4;

        for (var i = 0; i < 80; i++)
        {
            uint f,
                k;
            if (i < 20)
            {
                f = (b & c) | ((~b) & d);
                k = 0x5A827999;
            }
            else if (i < 40)
            {
                f = b ^ c ^ d;
                k = 0x6ED9EBA1;
            }
            else if (i < 60)
            {
                f = (b & c) | (b & d) | (c & d);
                k = 0x8F1BBCDC;
            }
            else
            {
                f = b ^ c ^ d;
                k = 0xCA62C1D6;
            }

            var temp = RotateLeft(a, 5) + f + e + k + w[i];
            e = d;
            d = c;
            c = RotateLeft(b, 30);
            b = a;
            a = temp;
        }

        _h0 += a;
        _h1 += b;
        _h2 += c;
        _h3 += d;
        _h4 += e;
    }

    private static uint RotateLeft(uint value, int bits) =>
        (value << bits) | (value >> (32 - bits));

    private static uint BinaryPrimitivesReadUInt32BigEndian(ReadOnlySpan<byte> source) =>
        ((uint)source[0] << 24) | ((uint)source[1] << 16) | ((uint)source[2] << 8) | source[3];

    private static void BinaryPrimitivesWriteUInt64BigEndian(Span<byte> destination, ulong value)
    {
        destination[0] = (byte)(value >> 56);
        destination[1] = (byte)(value >> 48);
        destination[2] = (byte)(value >> 40);
        destination[3] = (byte)(value >> 32);
        destination[4] = (byte)(value >> 24);
        destination[5] = (byte)(value >> 16);
        destination[6] = (byte)(value >> 8);
        destination[7] = (byte)value;
    }

    private static void WriteUInt32BigEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    [InlineArray(64)]
    private struct Sha1Block
    {
        private byte _element0;
    }
}
