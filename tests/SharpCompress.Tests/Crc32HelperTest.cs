using System;
using AwesomeAssertions;
using SharpCompress.Algorithms;
using SharpCompress.Crypto;
using Xunit;

namespace SharpCompress.Test;

public class Crc32HelperTest
{
    // Reference IEEE CRC-32 table implementation (poly 0xEDB88320), matching the
    // historical SharpCompress RarCRC / Deflate CRC32 / LZMA Crc scalar loops.
    private static readonly uint[] ReferenceTable = CreateReferenceTable();

    private static uint[] CreateReferenceTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++)
            {
                if ((c & 1) != 0)
                {
                    c = (c >> 1) ^ 0xEDB88320;
                }
                else
                {
                    c >>= 1;
                }
            }
            table[i] = c;
        }
        return table;
    }

    private static uint ReferenceAppend(uint startCrc, ReadOnlySpan<byte> data)
    {
        var crc = startCrc;
        foreach (var b in data)
        {
            crc = ReferenceTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        }
        return crc;
    }

    [Fact]
    public void Append_Empty_ReturnsSameState()
    {
        const uint state = 0x12345678;
        Crc32Helper.Append(state, ReadOnlySpan<byte>.Empty).Should().Be(state);
    }

    [Fact]
    public void Append_EmptyInput_MatchesReference()
    {
        var data = ReadOnlySpan<byte>.Empty;
        var expected = ReferenceAppend(0xFFFFFFFF, data);
        Crc32Helper.Append(0xFFFFFFFF, data).Should().Be(expected);
        (~expected).Should().Be(0u); // finalized CRC of empty is 0
    }

    [Fact]
    public void Append_SingleByte_MatchesReference()
    {
        Span<byte> data = [0x42];
        var expected = ReferenceAppend(0xFFFFFFFF, data);
        Crc32Helper.Append(0xFFFFFFFF, data).Should().Be(expected);
        Crc32Helper.Append(0xFFFFFFFF, (byte)0x42).Should().Be(expected);
    }

    [Fact]
    public void Append_4093PseudoRandomBytes_MatchesReference()
    {
        var data = new byte[4093];
        var rng = new Random(42);
        rng.NextBytes(data);

        var expected = ReferenceAppend(0xFFFFFFFF, data);
        var actual = Crc32Helper.Append(0xFFFFFFFF, data);
        actual.Should().Be(expected);
    }

    [Fact]
    public void Append_Chunked_MatchesFullBuffer()
    {
        var data = new byte[1024];
        new Random(7).NextBytes(data);

        var full = Crc32Helper.Append(0xFFFFFFFF, data);
        var state = 0xFFFFFFFFu;
        state = Crc32Helper.Append(state, data.AsSpan(0, 100));
        state = Crc32Helper.Append(state, data.AsSpan(100, 400));
        state = Crc32Helper.Append(state, data.AsSpan(500));
        state.Should().Be(full);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1024)]
    [InlineData(4093)]
    public void Crc32Stream_Compute_MatchesCrc32Helper(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        const uint seed = 0xFFFFFFFF;

        var helperState = Crc32Helper.Append(seed, data);
        var computeResult = Crc32Stream.Compute(Crc32Stream.DEFAULT_POLYNOMIAL, seed, data);

        computeResult.Should().Be(~helperState);
        Crc32Stream.Compute(seed, data).Should().Be(~helperState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1024)]
    [InlineData(4093)]
    public void Crc32Stream_CalculateCrc_MatchesCrc32Helper(int length)
    {
        var data = new byte[length];
        new Random(length + 13).NextBytes(data);
        const uint seed = 0xFFFFFFFF;
        var table = Crc32Stream.InitializeTable(Crc32Stream.DEFAULT_POLYNOMIAL);

        var tableResult = Crc32Stream.CalculateCrc(table, seed, data);
        var helperResult = Crc32Helper.Append(seed, data);

        tableResult.Should().Be(helperResult);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x42)]
    [InlineData(0xFF)]
    public void Crc32Stream_CalculateCrc_SingleByte_MatchesCrc32Helper(byte value)
    {
        const uint seed = 0xABCDEF01;
        var table = Crc32Stream.InitializeTable(Crc32Stream.DEFAULT_POLYNOMIAL);

        var tableResult = Crc32Stream.CalculateCrc(table, seed, value);
        var helperResult = Crc32Helper.Append(seed, value);

        tableResult.Should().Be(helperResult);
    }
}
