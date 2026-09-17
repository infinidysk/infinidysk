using System.IO.Hashing;
using System.Security.Cryptography;
using MemoryPack;

namespace NzbWebDAV.Models;

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class Par2FileProof
{
    [MemoryPackOrder(0)]
    public long FileLength { get; set; }

    [MemoryPackOrder(1)]
    public int SliceSize { get; set; }

    [MemoryPackOrder(2)]
    public byte[] SliceMd5 { get; set; } = [];

    [MemoryPackOrder(3)]
    public uint[] SliceCrc32 { get; set; } = [];

    [MemoryPackOrder(4)]
    public byte[] FileId { get; set; } = [];

    [MemoryPackOrder(5)]
    public byte[] FileHash { get; set; } = [];

    [MemoryPackOrder(6)]
    public byte[] File16kHash { get; set; } = [];

    public bool IsValidFor(long fileLength)
    {
        if (fileLength <= 0 || FileLength != fileLength
            || SliceSize <= 0 || SliceSize > 32 * 1024 * 1024 || SliceSize % 4 != 0
            || FileId is not { Length: 16 } || FileHash is not { Length: 16 }
            || SliceMd5 is null || SliceCrc32 is null)
            return false;

        var sliceCount = (fileLength - 1) / SliceSize + 1;
        return sliceCount <= int.MaxValue / 16
            && SliceMd5.Length == sliceCount * 16
            && SliceCrc32.Length == sliceCount;
    }

#pragma warning disable CA5351 // PAR2 specifies MD5 for slice integrity.
    public bool VerifyPrefix(ReadOnlySpan<byte> prefix)
    {
        if (!IsValidFor(FileLength) || File16kHash is not { Length: 16 }
            || prefix.Length != Math.Min(16384, FileLength))
            return false;
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(prefix, hash);
        return hash.SequenceEqual(File16kHash);
    }

    public bool VerifySlice(ReadOnlySpan<byte> paddedSlice, int sliceIndex)
    {
        if (!IsValidFor(FileLength) || paddedSlice.Length != SliceSize
            || sliceIndex < 0 || sliceIndex >= SliceCrc32.Length)
            return false;

        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(paddedSlice, hash);
        return hash.SequenceEqual(SliceMd5.AsSpan(sliceIndex * 16, 16))
            && Crc32.HashToUInt32(paddedSlice) == SliceCrc32[sliceIndex];
    }
#pragma warning restore CA5351
}