using System.IO.Hashing;
using System.Security.Cryptography;
using MemoryPack;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Par2Recovery;

#pragma warning disable CA5351 // PAR2 specifies MD5 for slice integrity.
public class Par2FileProofTests
{
    [Fact]
    public void VerifySlice_MatchesProtocolHashVector()
    {
        var proof = new Par2FileProof
        {
            FileLength = 4,
            SliceSize = 4,
            SliceMd5 = Convert.FromHexString("81DC9BDB52D04DC20036DBD8313ED055"),
            SliceCrc32 = [0x9BE3E0A3],
            FileId = new byte[16],
            FileHash = Convert.FromHexString("81DC9BDB52D04DC20036DBD8313ED055"),
        };
        Assert.True(proof.VerifySlice("1234"u8, 0));
    }

    [Fact]
    public void VerifySlice_RequiresBothHashesAndFullPadding()
    {
        byte[] padded = [1, 2, 3, 0];
        var proof = CreateProof(padded);

        Assert.True(proof.IsValidFor(3));
        Assert.True(proof.VerifySlice(padded, 0));
        Assert.False(proof.VerifySlice(padded.AsSpan(0, 3), 0));
        Assert.False(proof.VerifySlice(padded, -1));
        Assert.False(proof.VerifySlice(padded, 1));
        Assert.False(proof.VerifySlice([1, 2, 3, 1], 0));
        proof.SliceCrc32[0] ^= 1;
        Assert.False(proof.VerifySlice(padded, 0));
        proof.SliceCrc32[0] ^= 1;
        proof.SliceMd5[0] ^= 1;
        Assert.False(proof.VerifySlice(padded, 0));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-1, 4)]
    [InlineData(long.MaxValue, 4)]
    [InlineData(3, 0)]
    [InlineData(3, -4)]
    [InlineData(3, 5)]
    [InlineData(3, 32 * 1024 * 1024 + 4)]
    public void IsValidFor_RejectsInvalidDimensions(long length, int sliceSize)
    {
        var proof = CreateProof([1, 2, 3, 0]);
        proof.FileLength = length;
        proof.SliceSize = sliceSize;
        Assert.False(proof.IsValidFor(length));
    }

    [Fact]
    public void IsValidFor_RequiresExactLengths()
    {
        var proof = CreateProof([1, 2, 3, 0]);
        Assert.False(proof.IsValidFor(4));
        proof.FileLength = 5;
        Assert.False(proof.IsValidFor(5));
        proof.FileLength = 3;
        proof.FileId = new byte[15];
        Assert.False(proof.IsValidFor(3));
        proof.FileId = new byte[16];
        proof.FileHash = new byte[17];
        Assert.False(proof.IsValidFor(3));
        proof.FileHash = new byte[16];
        proof.SliceMd5 = new byte[17];
        Assert.False(proof.IsValidFor(3));
        proof.SliceMd5 = new byte[16];
        proof.SliceCrc32 = new uint[2];
        Assert.False(proof.IsValidFor(3));
    }

    [Fact]
    public void MemoryPack_RoundTripsUsableProof()
    {
        byte[] padded = [1, 2, 3, 0];
        var proof = CreateProof(padded);
        var restored = MemoryPackSerializer.Deserialize<Par2FileProof>(MemoryPackSerializer.Serialize(proof));
        Assert.NotNull(restored);
        Assert.True(restored.IsValidFor(3));
        Assert.True(restored.VerifySlice(padded, 0));
    }

    private static Par2FileProof CreateProof(byte[] padded) => new()
    {
        FileLength = 3,
        SliceSize = 4,
        SliceMd5 = MD5.HashData(padded),
        SliceCrc32 = [Crc32.HashToUInt32(padded)],
        FileId = new byte[16],
        FileHash = MD5.HashData(padded.AsSpan(0, 3)),
    };
}
#pragma warning restore CA5351