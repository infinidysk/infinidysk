using MemoryPack;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Tests.Database;

public class Par2ProofSerializationTests
{
    [Fact]
    public async Task DavNzbFile_RoundTripPreservesProofAndFinalPaddedSlice()
    {
        var data = CreateFileData();
        var proof = await ReadProof(data);
        var original = new DavNzbFile
        {
            SegmentIds = ["verified@example.com"],
            VerificationProof = proof,
        };

        var restored = MemoryPackSerializer.Deserialize<DavNzbFile>(MemoryPackSerializer.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.SegmentIds, restored.SegmentIds);
        AssertRoundTrippedProof(proof, restored.VerificationProof, data);
    }

    [Fact]
    public async Task DavMultipartFile_FilePartRoundTripPreservesProofAndFinalPaddedSlice()
    {
        var data = CreateFileData();
        var proof = await ReadProof(data);
        var original = new DavMultipartFile
        {
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["verified@example.com"],
                        SegmentIdByteRange = new LongRange(0, data.Length),
                        FilePartByteRange = new LongRange(0, data.Length),
                        VerificationProof = proof,
                    },
                ],
            },
        };

        var restored = MemoryPackSerializer.Deserialize<DavMultipartFile>(MemoryPackSerializer.Serialize(original));

        Assert.NotNull(restored);
        var part = Assert.Single(restored.Metadata.FileParts);
        Assert.Equal(original.Metadata.FileParts[0].SegmentIds, part.SegmentIds);
        Assert.Equal(new LongRange(0, data.Length), part.FilePartByteRange);
        AssertRoundTrippedProof(proof, part.VerificationProof, data);
    }

    [Fact]
    public async Task DavMultipartFile_PendingPartRoundTripPreservesProofAndFinalPaddedSlice()
    {
        var data = CreateFileData();
        var proof = await ReadProof(data);
        var original = new DavMultipartFile
        {
            Metadata = new DavMultipartFile.Meta
            {
                IsLazy = true,
                PendingParts =
                [
                    new DavMultipartFile.PendingPart
                    {
                        SegmentIds = ["pending@example.com"],
                        SegmentIdByteRange = new LongRange(0, data.Length),
                        EstimatedDataSize = data.Length,
                        VerificationProof = proof,
                    },
                ],
            },
        };

        var restored = MemoryPackSerializer.Deserialize<DavMultipartFile>(MemoryPackSerializer.Serialize(original));

        Assert.NotNull(restored);
        Assert.True(restored.Metadata.IsLazy);
        var part = Assert.Single(restored.Metadata.PendingParts);
        Assert.Equal(original.Metadata.PendingParts[0].SegmentIds, part.SegmentIds);
        Assert.Equal(data.Length, part.EstimatedDataSize);
        AssertRoundTrippedProof(proof, part.VerificationProof, data);
    }

    private static byte[] CreateFileData() => Enumerable.Range(0, 4103).Select(value => (byte)value).ToArray();

    private static async Task<Par2FileProof> ReadProof(byte[] data)
    {
        var (index, _) = Par2TestEncoder.EncodeSet("verified.mkv", data, 4096, []);
        using var stream = new MemoryStream(index);
        var descriptors = new List<FileDesc>();
        await foreach (var descriptor in Par2.ReadVerifiedFileDescriptions(stream))
            descriptors.Add(descriptor);
        return Assert.IsType<Par2FileProof>(Assert.Single(descriptors).VerificationProof);
    }

    private static void AssertRoundTrippedProof(Par2FileProof original, Par2FileProof? restored, byte[] data)
    {
        Assert.NotNull(restored);
        Assert.NotSame(original, restored);
        Assert.Equal(original.FileLength, restored.FileLength);
        Assert.Equal(original.SliceSize, restored.SliceSize);
        Assert.Equal(original.FileId, restored.FileId);
        Assert.Equal(original.FileHash, restored.FileHash);
        Assert.Equal(original.File16kHash, restored.File16kHash);
        Assert.Equal(original.SliceMd5, restored.SliceMd5);
        Assert.Equal(original.SliceCrc32, restored.SliceCrc32);
        Assert.True(restored.IsValidFor(data.Length));
        Assert.True(restored.VerifyPrefix(data));
        Assert.False(restored.VerifyPrefix(data.AsSpan(1)));
        Assert.True(restored.VerifySlice(data.AsSpan(0, 4096), 0));
        var padded = new byte[4096];
        data.AsSpan(4096).CopyTo(padded);
        Assert.True(restored.VerifySlice(padded, 1));
        Assert.False(restored.VerifySlice(data.AsSpan(4096), 1));
        padded[^1] = 1;
        Assert.False(restored.VerifySlice(padded, 1));
    }
}