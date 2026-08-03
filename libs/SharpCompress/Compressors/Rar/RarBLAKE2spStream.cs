using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpCompress.Common;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Compressors.Rar;

internal partial class RarBLAKE2spStream : RarStream
{
    private readonly MultiVolumeReadOnlyStreamBase readStream;
    private readonly bool disableCRCCheck;

    private const int BLAKE2S_NUM_ROUNDS = 10;
    private const uint BLAKE2S_FINAL_FLAG = ~(uint)0;
    private const int BLAKE2S_BLOCK_SIZE = 64;
    private const int BLAKE2S_DIGEST_SIZE = 32;
    private const int BLAKE2SP_PARALLEL_DEGREE = 8;
    private const int BLAKE2S_INIT_IV_SIZE = 8;

    private static ReadOnlySpan<uint> Blake2sIv =>
        [
            0x6A09E667U,
            0xBB67AE85U,
            0x3C6EF372U,
            0xA54FF53AU,
            0x510E527FU,
            0x9B05688CU,
            0x1F83D9ABU,
            0x5BE0CD19U,
        ];

    // Flat RVA-static sigma table: Sigma[round * 16 + i]
    private static ReadOnlySpan<byte> Sigma =>
        [
            0,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            9,
            10,
            11,
            12,
            13,
            14,
            15,
            14,
            10,
            4,
            8,
            9,
            15,
            13,
            6,
            1,
            12,
            0,
            2,
            11,
            7,
            5,
            3,
            11,
            8,
            12,
            0,
            5,
            2,
            15,
            13,
            10,
            14,
            3,
            6,
            7,
            1,
            9,
            4,
            7,
            9,
            3,
            1,
            13,
            12,
            11,
            14,
            2,
            6,
            5,
            10,
            4,
            0,
            15,
            8,
            9,
            0,
            5,
            7,
            2,
            4,
            10,
            15,
            14,
            1,
            11,
            12,
            6,
            8,
            3,
            13,
            2,
            12,
            6,
            10,
            0,
            11,
            8,
            3,
            4,
            13,
            7,
            5,
            15,
            14,
            1,
            9,
            12,
            5,
            1,
            15,
            14,
            13,
            4,
            10,
            0,
            7,
            6,
            3,
            9,
            2,
            8,
            11,
            13,
            11,
            7,
            14,
            12,
            1,
            3,
            9,
            5,
            0,
            15,
            4,
            8,
            6,
            2,
            10,
            6,
            15,
            14,
            9,
            11,
            3,
            0,
            8,
            12,
            2,
            13,
            7,
            1,
            4,
            10,
            5,
            10,
            2,
            8,
            4,
            7,
            6,
            1,
            5,
            15,
            11,
            9,
            14,
            3,
            12,
            13,
            0,
        ];

    [InlineArray(BLAKE2S_INIT_IV_SIZE)]
    private struct UInt8Buffer
    {
        private uint _element0;
    }

    [InlineArray(2)]
    private struct UInt2Buffer
    {
        private uint _element0;
    }

    [InlineArray(BLAKE2S_BLOCK_SIZE)]
    private struct Byte64Buffer
    {
        private byte _element0;
    }

    [InlineArray(BLAKE2SP_PARALLEL_DEGREE)]
    private struct Blake2sLeafBuffer
    {
        private Blake2sState _element0;
    }

    private struct Blake2sState
    {
        public UInt8Buffer H;
        public UInt2Buffer T;
        public UInt2Buffer F;
        public Byte64Buffer B;
        public int BufferPosition;
        public uint LastNodeFlag;
    }

    private sealed class Blake2spState
    {
        internal Blake2sLeafBuffer Leaves;
        internal Blake2sState Root;
        internal int BufferPosition;
    }

    private Blake2spState? _blake2sp;
    private byte[]? _hash;

    private RarBLAKE2spStream(
        IRarUnpack unpack,
        FileHeader fileHeader,
        MultiVolumeReadOnlyStreamBase readStream,
        bool ownsUnpack,
        Action? onDispose
    )
        : base(unpack, fileHeader, readStream, ownsUnpack, onDispose)
    {
        this.readStream = readStream;

        // Encrypted entries skip CRC: UnRAR may XOR the hash with the encryption key; exact behavior is unclear.
        disableCRCCheck = fileHeader.IsEncrypted;
        this._blake2sp = CreateBlake2sp();
    }

    public static RarBLAKE2spStream Create(
        IRarUnpack unpack,
        FileHeader fileHeader,
        MultiVolumeReadOnlyStream readStream,
        bool ownsUnpack = false,
        Action? onDispose = null
    )
    {
        var stream = new RarBLAKE2spStream(unpack, fileHeader, readStream, ownsUnpack, onDispose);
        return stream;
    }

    public byte[] GetCrc() =>
        this._hash
        ?? throw new InvalidOperationException(
            "hash not computed, has the stream been fully drained?"
        );

    private static void ResetCrc(ref Blake2sState hash)
    {
        Blake2sIv.CopyTo(hash.H);
        hash.T[0] = 0;
        hash.T[1] = 0;
        hash.F[0] = 0;
        hash.F[1] = 0;
        hash.BufferPosition = 0;
        hash.LastNodeFlag = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G(
        Span<uint> m,
        ReadOnlySpan<byte> sigma,
        int i,
        ref uint a,
        ref uint b,
        ref uint c,
        ref uint d
    )
    {
        a += b + m[sigma[2 * i]];
        d ^= a;
        d = (d >> 16) | (d << 16);
        c += d;
        b ^= c;
        b = (b >> 12) | (b << 20);

        a += b + m[sigma[2 * i + 1]];
        d ^= a;
        d = (d >> 8) | (d << 24);
        c += d;
        b ^= c;
        b = (b >> 7) | (b << 25);
    }

    private static void Compress(ref Blake2sState hash)
    {
        Span<uint> m = stackalloc uint[16];
        ReadOnlySpan<byte> block = hash.B;
        for (var i = 0; i < 16; i++)
        {
            m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
        }

        Span<uint> v = stackalloc uint[16];
        for (var i = 0; i < 8; i++)
        {
            v[i] = hash.H[i];
        }

        var iv = Blake2sIv;
        v[8] = iv[0];
        v[9] = iv[1];
        v[10] = iv[2];
        v[11] = iv[3];

        v[12] = hash.T[0] ^ iv[4];
        v[13] = hash.T[1] ^ iv[5];
        v[14] = hash.F[0] ^ iv[6];
        v[15] = hash.F[1] ^ iv[7];

        var sigma = Sigma;
        for (var r = 0; r < BLAKE2S_NUM_ROUNDS; r++)
        {
            var round = sigma.Slice(r * 16, 16);
            G(m, round, 0, ref v[0], ref v[4], ref v[8], ref v[12]);
            G(m, round, 1, ref v[1], ref v[5], ref v[9], ref v[13]);
            G(m, round, 2, ref v[2], ref v[6], ref v[10], ref v[14]);
            G(m, round, 3, ref v[3], ref v[7], ref v[11], ref v[15]);
            G(m, round, 4, ref v[0], ref v[5], ref v[10], ref v[15]);
            G(m, round, 5, ref v[1], ref v[6], ref v[11], ref v[12]);
            G(m, round, 6, ref v[2], ref v[7], ref v[8], ref v[13]);
            G(m, round, 7, ref v[3], ref v[4], ref v[9], ref v[14]);
        }

        for (var i = 0; i < 8; i++)
        {
            hash.H[i] ^= v[i] ^ v[i + 8];
        }
    }

    private static void Update(ref Blake2sState hash, ReadOnlySpan<byte> data)
    {
        while (data.Length != 0)
        {
            var pos = hash.BufferPosition;
            var chunkSize = BLAKE2S_BLOCK_SIZE - pos;
            Span<byte> buffer = hash.B;
            if (data.Length <= chunkSize)
            {
                data.CopyTo(buffer.Slice(pos));
                hash.BufferPosition += data.Length;
                return;
            }
            data.Slice(0, chunkSize).CopyTo(buffer.Slice(pos));
            hash.T[0] += BLAKE2S_BLOCK_SIZE;
            hash.T[1] += hash.T[0] < BLAKE2S_BLOCK_SIZE ? 1U : 0U;
            Compress(ref hash);
            hash.BufferPosition = 0;
            data = data.Slice(chunkSize);
        }
    }

    private static void Final(ref Blake2sState hash, Span<byte> output)
    {
        hash.T[0] += (uint)hash.BufferPosition;
        hash.T[1] += hash.T[0] < hash.BufferPosition ? 1U : 0U;
        hash.F[0] = BLAKE2S_FINAL_FLAG;
        hash.F[1] = hash.LastNodeFlag;
        Span<byte> buffer = hash.B;
        buffer.Slice(hash.BufferPosition).Clear();
        Compress(ref hash);

        for (var i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), hash.H[i]);
        }
    }

    private static Blake2spState CreateBlake2sp()
    {
        var blake2sp = new Blake2spState();

        for (var i = 0; i < BLAKE2SP_PARALLEL_DEGREE; i++)
        {
            ref var blake2S = ref blake2sp.Leaves[i];
            ResetCrc(ref blake2S);

            // word[0]: digest_length | (fanout<<16) | (depth<<24)
            blake2S.H[0] ^= BLAKE2S_DIGEST_SIZE | (BLAKE2SP_PARALLEL_DEGREE << 16) | (2 << 24);
            // word[2]: node_offset = leaf index
            blake2S.H[2] ^= (uint)i;
            // word[3]: inner_length in bits 24-31
            blake2S.H[3] ^= BLAKE2S_DIGEST_SIZE << 24;
        }

        blake2sp.Leaves[BLAKE2SP_PARALLEL_DEGREE - 1].LastNodeFlag = BLAKE2S_FINAL_FLAG;
        return blake2sp;
    }

    private static void Update(Blake2spState hash, ReadOnlySpan<byte> data)
    {
        var pos = hash.BufferPosition;
        while (data.Length != 0)
        {
            var index = pos / BLAKE2S_BLOCK_SIZE;
            var chunkSize = BLAKE2S_BLOCK_SIZE - (pos & (BLAKE2S_BLOCK_SIZE - 1));
            if (chunkSize > data.Length)
            {
                chunkSize = data.Length;
            }
            Update(ref hash.Leaves[index], data.Slice(0, chunkSize));
            data = data.Slice(chunkSize);
            pos = (pos + chunkSize) & (BLAKE2S_BLOCK_SIZE * BLAKE2SP_PARALLEL_DEGREE - 1);
        }
        hash.BufferPosition = pos;
    }

    private static byte[] Final(Blake2spState blake2sp)
    {
        ref var blake2s = ref blake2sp.Root;
        ResetCrc(ref blake2s);

        // word[0]: digest_length | (fanout<<16) | (depth<<24)  — same as leaves
        blake2s.H[0] ^= BLAKE2S_DIGEST_SIZE | (BLAKE2SP_PARALLEL_DEGREE << 16) | (2 << 24);
        // word[3]: node_depth=1 (bits 16-23), inner_length=32 (bits 24-31)
        blake2s.H[3] ^= (1 << 16) | (BLAKE2S_DIGEST_SIZE << 24);
        blake2s.LastNodeFlag = BLAKE2S_FINAL_FLAG;

        Span<byte> digest = stackalloc byte[BLAKE2S_DIGEST_SIZE];
        for (var i = 0; i < BLAKE2SP_PARALLEL_DEGREE; i++)
        {
            Final(ref blake2sp.Leaves[i], digest);
            Update(ref blake2s, digest);
        }

        Final(ref blake2s, digest);
        return digest.ToArray();
    }

    private void EnsureHash()
    {
        if (this._hash is null)
        {
            this._hash = Final(this._blake2sp!);
            // prevent incorrect usage past hash finality by failing fast
            this._blake2sp = null;
        }
    }

    /// <summary>
    /// Hashes <paramref name="data"/> with BLAKE2sp for unit tests / digests.
    /// </summary>
    internal static byte[] HashBlake2sp(ReadOnlySpan<byte> data)
    {
        var state = CreateBlake2sp();
        Update(state, data);
        return Final(state);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var result = base.Read(buffer, offset, count);
        if (result != 0)
        {
            Update(this._blake2sp!, new ReadOnlySpan<byte>(buffer, offset, result));
        }
        else
        {
            EnsureHash();
            if (!disableCRCCheck && !GetCrc().SequenceEqual(readStream.CurrentCrc) && count != 0)
            {
                // NOTE: we use the last FileHeader in a multipart volume to check CRC
                throw new InvalidFormatException("file crc mismatch");
            }
        }

        return result;
    }

    public override int Read(Span<byte> buffer)
    {
        var result = base.Read(buffer);
        if (result != 0)
        {
            Update(this._blake2sp!, buffer.Slice(0, result));
        }
        else
        {
            EnsureHash();
            if (
                !disableCRCCheck
                && !GetCrc().SequenceEqual(readStream.CurrentCrc)
                && buffer.Length != 0
            )
            {
                throw new InvalidFormatException("file crc mismatch");
            }
        }

        return result;
    }
}
