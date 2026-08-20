using System.Buffers.Binary;
using System.Text;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Queue.NestedRarExpansion;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Queue;

public class NestedRarExpansionStepTests
{
    [Fact]
    public void RangeMapper_MapsSpanAcrossOuterVolumes()
    {
        var first = Segment("inner.rar", headerPart: 0, filenamePart: 1, start: 10, length: 50, uncompressed: 90);
        var second = Segment("inner.rar", headerPart: 1, filenamePart: 2, start: 0, length: 40, uncompressed: 90);

        var mapped = NestedRarRangeMapper.Map(
            LongRange.FromStartAndSize(40, 30),
            [first, second],
            pathWithinArchive: "movie.mkv",
            archiveName: "inner",
            partNumber: new RarProcessor.PartNumber { PartNumberFromHeader = -1, PartNumberFromFilename = -1 },
            aesParams: null,
            fileUncompressedSize: 30,
            releaseDate: DateTimeOffset.UnixEpoch);

        Assert.Equal(2, mapped.Length);
        Assert.Equal(LongRange.FromStartAndSize(50, 10), mapped[0].ByteRangeWithinPart);
        Assert.Equal(first.PartNumber, mapped[0].PartNumber);
        Assert.Equal(LongRange.FromStartAndSize(0, 20), mapped[1].ByteRangeWithinPart);
        Assert.Equal(second.PartNumber, mapped[1].PartNumber);
        Assert.All(mapped, segment => Assert.Equal("movie.mkv", segment.PathWithinArchive));
    }

    [Fact]
    public async Task Expand_ReplacesStoredNestedRarWithInnerFiles()
    {
        var moviePayload = "hello-nested-mkv"u8.ToArray();
        // Composed stream for a nested member is the member payload itself
        // (already sliced out of the outer archive by RarProcessor).
        var nestedRar = BuildRar4FirstVolume("movie.mkv", moviePayload);
        var nested = Segment(
            "inner.rar",
            headerPart: -1,
            filenamePart: -1,
            start: 0,
            length: nestedRar.Length,
            uncompressed: nestedRar.Length,
            partSize: nestedRar.Length);

        var expanded = await NestedRarExpansionStep.ExpandSegmentsAsync(
            [nested],
            (_, _) => Task.FromResult(Frozen(nestedRar)),
            password: null,
            CancellationToken.None);

        Assert.DoesNotContain(expanded, segment => FilenameUtil.IsRarFile(segment.PathWithinArchive));
        var movie = Assert.Single(expanded, segment => segment.PathWithinArchive == "movie.mkv");
        Assert.Equal(moviePayload.Length, movie.FileUncompressedSize);
        Assert.Equal(moviePayload.Length, movie.ByteRangeWithinPart.Count);
    }

    [Fact]
    public async Task Expand_KeepsOpaqueWhenInnerIsCompressed()
    {
        var payload = "compressed-payload"u8.ToArray();
        var compressedNested = BuildRar4FirstVolume("movie.mkv", payload, method: 0x31);
        var nested = Segment(
            "inner.rar",
            headerPart: -1,
            filenamePart: -1,
            start: 0,
            length: compressedNested.Length,
            uncompressed: compressedNested.Length,
            partSize: compressedNested.Length);

        var expanded = await NestedRarExpansionStep.ExpandSegmentsAsync(
            [nested],
            (_, _) => Task.FromResult(Frozen(compressedNested)),
            password: null,
            CancellationToken.None);

        var kept = Assert.Single(expanded);
        Assert.Equal("inner.rar", kept.PathWithinArchive);
    }

    [Fact]
    public async Task Expand_SkipsEncryptedOuterMembers()
    {
        var outer = Segment(
            "secret.rar",
            headerPart: -1,
            filenamePart: -1,
            start: 0,
            length: 32,
            uncompressed: 32);
        outer = new RarProcessor.StoredFileSegment
        {
            NzbFile = outer.NzbFile,
            PartSize = outer.PartSize,
            ArchiveName = outer.ArchiveName,
            PartNumber = outer.PartNumber,
            ReleaseDate = outer.ReleaseDate,
            PathWithinArchive = outer.PathWithinArchive,
            ByteRangeWithinPart = outer.ByteRangeWithinPart,
            AesParams = new AesParams { Key = new byte[16], Iv = new byte[16], DecodedSize = 32 },
            FileUncompressedSize = outer.FileUncompressedSize,
        };

        var expanded = await NestedRarExpansionStep.ExpandSegmentsAsync(
            [outer],
            (_, _) => throw new InvalidOperationException("should not open encrypted nested rar"),
            password: null,
            CancellationToken.None);

        Assert.Same(outer, Assert.Single(expanded));
    }

    [Fact]
    public async Task Expand_RespectsDepthLimit()
    {
        var leaf = "leaf"u8.ToArray();
        var midRar = BuildRar4FirstVolume("movie.mkv", leaf);
        var outerNested = BuildRar4FirstVolume("mid.rar", midRar);
        var outer = Segment(
            "outer.rar",
            headerPart: -1,
            filenamePart: -1,
            start: 0,
            length: outerNested.Length,
            uncompressed: outerNested.Length,
            partSize: outerNested.Length);

        // Depth 1: outer.rar → mid.rar (still rar, stop)
        var depthOne = await NestedRarExpansionStep.ExpandSegmentsAsync(
            [outer],
            (_, _) => Task.FromResult(Frozen(outerNested)),
            password: null,
            CancellationToken.None,
            maxDepth: 1);
        Assert.Equal("mid.rar", Assert.Single(depthOne).PathWithinArchive);

        // Depth 2: reconstruct mid.rar from remapped ranges for the second open.
        var opens = 0;
        var depthTwo = await NestedRarExpansionStep.ExpandSegmentsAsync(
            [outer],
            (segments, _) =>
            {
                opens++;
                if (opens == 1)
                    return Task.FromResult(Frozen(outerNested));

                return Task.FromResult(ComposeSlices(outerNested, segments));
            },
            password: null,
            CancellationToken.None,
            maxDepth: 2);

        Assert.Equal("movie.mkv", Assert.Single(depthTwo).PathWithinArchive);
        Assert.Equal(2, opens);
    }

    [Fact]
    public async Task ExpandAsync_PreservesNonRarProcessorResults()
    {
        var moviePayload = "x"u8.ToArray();
        var nestedRar = BuildRar4FirstVolume("movie.mkv", moviePayload);
        var nested = Segment(
            "inner.rar",
            headerPart: -1,
            filenamePart: -1,
            start: 0,
            length: nestedRar.Length,
            uncompressed: nestedRar.Length,
            partSize: nestedRar.Length);

        var other = new FileProcessorResultMarker();
        var results = await NestedRarExpansionStep.ExpandAsync(
            [
                other,
                new RarProcessor.Result { StoredFileSegments = [nested] },
            ],
            (_, _) => Task.FromResult(Frozen(nestedRar)),
            password: null,
            CancellationToken.None);

        Assert.Contains(other, results);
        var rar = Assert.Single(results.OfType<RarProcessor.Result>());
        Assert.Equal("movie.mkv", Assert.Single(rar.StoredFileSegments).PathWithinArchive);
    }

    private static Stream Frozen(byte[] bytes) => new MemoryStream(bytes, writable: false);

    private static Stream ComposeSlices(
        byte[] source, IEnumerable<RarProcessor.StoredFileSegment> segments)
    {
        var composed = new MemoryStream();
        foreach (var segment in segments)
        {
            var slice = source.AsSpan(
                (int)segment.ByteRangeWithinPart.StartInclusive,
                (int)segment.ByteRangeWithinPart.Count);
            composed.Write(slice);
        }

        composed.Position = 0;
        return composed;
    }

    private sealed class FileProcessorResultMarker : BaseProcessor.Result;

    private static RarProcessor.StoredFileSegment Segment(
        string pathWithinArchive,
        int headerPart,
        int filenamePart,
        long start,
        long length,
        long uncompressed,
        long? partSize = null)
    {
        return new RarProcessor.StoredFileSegment
        {
            NzbFile = new NzbFile { Subject = "archive" },
            PartSize = partSize ?? length,
            ArchiveName = "archive",
            PartNumber = new RarProcessor.PartNumber
            {
                PartNumberFromHeader = headerPart,
                PartNumberFromFilename = filenamePart,
            },
            ReleaseDate = DateTimeOffset.UnixEpoch,
            PathWithinArchive = pathWithinArchive,
            ByteRangeWithinPart = LongRange.FromStartAndSize(start, length),
            AesParams = null,
            FileUncompressedSize = uncompressed,
        };
    }

    private static byte[] BuildRar4FirstVolume(string fileName, byte[] payload, byte method = 0x30)
    {
        using var ms = new MemoryStream();
        ms.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        // Archive header (HEAD_SIZE=13 including CRC).
        {
            Span<byte> body = stackalloc byte[11];
            body[0] = 0x73;
            BinaryPrimitives.WriteUInt16LittleEndian(body[1..], 0x0000);
            BinaryPrimitives.WriteUInt16LittleEndian(body[3..], 13);
            BinaryPrimitives.WriteUInt16LittleEndian(body[5..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(body[7..], 0);
            WriteHeader(ms, body);
        }

        var nameBytes = Encoding.ASCII.GetBytes(fileName);
        var headSize = (ushort)(32 + nameBytes.Length);
        {
            var body = new byte[headSize - 2];
            var o = 0;
            body[o++] = 0x74;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), 0x8000); // HAS_DATA
            o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), headSize);
            o += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), (uint)payload.Length); // ADD_SIZE
            o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), (uint)payload.Length); // UNP_SIZE
            o += 4;
            body[o++] = 2; // HostOS Unix
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0); // FileCRC
            o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0); // FileTime
            o += 4;
            body[o++] = 20; // UnpVer
            body[o++] = method;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), (ushort)nameBytes.Length);
            o += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0); // Attr
            o += 4;
            nameBytes.CopyTo(body.AsSpan(o));
            WriteHeader(ms, body);
        }

        ms.Write(payload);
        return ms.ToArray();
    }

    private static void WriteHeader(Stream stream, ReadOnlySpan<byte> bodyWithoutCrc)
    {
        var crc = RarCrc16(bodyWithoutCrc);
        Span<byte> hdr = stackalloc byte[bodyWithoutCrc.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(hdr, crc);
        bodyWithoutCrc.CopyTo(hdr[2..]);
        stream.Write(hdr);
    }

    private static ushort RarCrc16(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return (ushort)(~crc);
    }
}
