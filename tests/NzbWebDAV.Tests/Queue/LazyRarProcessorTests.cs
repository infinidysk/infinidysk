using System.Buffers.Binary;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Queue;

public class LazyRarProcessorTests
{
    [Fact]
    public async Task ProcessAsync_UnknownUncompressedSize_ReturnsNull()
    {
        const int packed = 200;
        var volumeBytes = BuildRar4SplitFirstVolume(
            "movie.mkv", packed, unchecked((int)0xffffffff));
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);
        var trailing = FileInfoFor("vol.r00", "r00@example.com", encodedBytes: 2_100, fileSize: null);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
        });

        var result = await new LazyRarProcessor([first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ProcessAsync_SingleVolumeCannotCoverUncompressedSize_ReturnsNull()
    {
        const int packed = 200;
        const int uncompressed = 10_000;
        var volumeBytes = BuildRar4SplitFirstVolume("movie.mkv", packed, uncompressed);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
        });

        var result = await new LazyRarProcessor([first], client, password: null, CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ProcessAsync_PartialSetCannotCoverUncompressedSize_ReturnsNull()
    {
        const int packed = 200;
        const int uncompressed = 10_000;
        var volumeBytes = BuildRar4SplitFirstVolume("movie.mkv", packed, uncompressed);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);
        // Pending encoded size far too small to cover remaining uncompressed bytes.
        var trailing = FileInfoFor("vol.r00", "r00@example.com", encodedBytes: 50, fileSize: null);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
        });

        var result = await new LazyRarProcessor([first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ProcessAsync_CompleteSetWithoutPar2Sizes_Mounts()
    {
        const int packed = 1_000;
        const int uncompressed = 3_000;
        var volumeBytes = BuildRar4SplitFirstVolume("movie.mkv", packed, uncompressed);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);
        // Encoded trailing size must cover remaining uncompressed bytes, but the
        // 0.95*encoded estimate (minus header guess) must not overshoot remaining
        // or LazyRar falls back before the coverage bound matters.
        var trailing = FileInfoFor("vol.r00", "r00@example.com", encodedBytes: 2_100, fileSize: null);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
        });

        var result = await new LazyRarProcessor([first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync() as LazyRarProcessor.Result;

        Assert.NotNull(result);
        Assert.Equal("movie.mkv", result!.PathInArchive);
        Assert.Equal(uncompressed, result!.TotalFileSize);
        Assert.Single(result!.PendingParts);
    }

    [Fact]
    public async Task ProcessAsync_UnderestimatedFirstVolumeSize_ContainsPackedRange()
    {
        const int packed = 1_000;
        const int uncompressed = 3_000;
        var volumeBytes = BuildRar4SplitFirstVolume("movie.mkv", packed, uncompressed);
        var underestimatedSize = volumeBytes.Length - 100;
        var first = FileInfoFor(
            "vol.rar",
            "first@example.com",
            volumeBytes.Length,
            underestimatedSize);
        var trailing = FileInfoFor("vol.r00", "r00@example.com", encodedBytes: 2_100, fileSize: null);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
        });

        var result = await new LazyRarProcessor([first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync() as LazyRarProcessor.Result;

        Assert.NotNull(result);
        Assert.True(result!.FirstPart.SegmentIdByteRange.Contains(result!.FirstPart.FilePartByteRange));
        Assert.Equal(result!.FirstPart.FilePartByteRange.EndExclusive, result!.FirstPart.SegmentIdByteRange.Count);
        Assert.True(result!.FirstPart.SegmentIdByteRange.Count > underestimatedSize);
    }

    [Fact]
    public async Task ProcessAsync_SniffsVideoExtensionFromStoredPayload()
    {
        var payload = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x00, 0x00, 0x00, 0x00 };
        const int packed = 1_000;
        const int uncompressed = 3_000;
        var volumeBytes = BuildRar4SplitFirstVolume(
            "b082fa0beaa644d3aa01045d5b8d0b36.xyz", packed, uncompressed, payload);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);
        var trailing = FileInfoFor("vol.r00", "r00@example.com", encodedBytes: 2_100, fileSize: null);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
        });

        var result = await new LazyRarProcessor([first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync() as LazyRarProcessor.Result;

        Assert.NotNull(result);
        Assert.Equal(".mkv", result!.SniffedVideoExtension);
    }

    [Fact]
    public async Task ProcessAsync_SniffFailureLeavesNullExtension()
    {
        const int packed = 1_000;
        const int uncompressed = 3_000;
        var volumeBytes = BuildRar4SplitFirstVolume("movie.mkv", packed, uncompressed);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);
        var trailing = FileInfoFor("vol.r00", "r00@example.com", encodedBytes: 2_100, fileSize: null);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
        });

        var result = await new LazyRarProcessor([first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync() as LazyRarProcessor.Result;

        Assert.NotNull(result);
        Assert.Null(result!.SniffedVideoExtension);
    }

    [Fact]
    public async Task BuildRar4SplitFirstVolume_IsFirstVolumeStoredSplit()
    {
        var bytes = BuildRar4SplitFirstVolume("movie.mkv", packedSize: 100, uncompressedSize: 500);
        await using var stream = new MemoryStream(bytes);
        var headers = await RarUtil.ReadHeadersUntilFirstFileAsync(stream, password: null, CancellationToken.None);
        var archive = Assert.Single(headers.OfType<SharpCompress.Common.Rar.Headers.IRarArchiveHeader>());
        var file = Assert.Single(headers.OfType<SharpCompress.Common.Rar.Headers.IRarFileHeader>());
        Assert.True(archive.IsFirstVolume);
        Assert.True(file.IsStored);
        Assert.Equal("movie.mkv", file.FileName);
        Assert.Equal(500u, file.UncompressedSize);
        Assert.Equal(100u, file.AdditionalDataSize);
    }

    private static GetFileInfosStep.FileInfo FileInfoFor(
        string fileName, string messageId, long encodedBytes, long? fileSize)
    {
        return new GetFileInfosStep.FileInfo
        {
            NzbFile = new NzbFile
            {
                Subject = $"\"{fileName}\" yEnc",
                Segments =
                {
                    new NzbSegment { MessageId = messageId, Bytes = encodedBytes }
                },
            },
            FileName = fileName,
            ReleaseDate = DateTimeOffset.UnixEpoch,
            FileSize = fileSize,
            IsRar = true,
        };
    }

    // Minimal RAR4 multi-volume first part: mark + archive(VOLUME|FIRSTVOLUME) +
    // stored file header (HAS_DATA|SPLIT_AFTER) with full UNP_SIZE + packed payload.
    private static byte[] BuildRar4SplitFirstVolume(
        string fileName, int packedSize, int uncompressedSize, ReadOnlySpan<byte> payloadPrefix = default)
    {
        using var ms = new MemoryStream();
        ms.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        // Archive header (HEAD_SIZE=13 including CRC).
        {
            Span<byte> body = stackalloc byte[11];
            body[0] = 0x73;
            // MHD_VOLUME (0x0001) | MHD_FIRSTVOLUME (0x0100)
            BinaryPrimitives.WriteUInt16LittleEndian(body[1..], 0x0101);
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
            // LHD_HAS_DATA (0x8000) | LHD_SPLIT_AFTER (0x0002)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), 0x8002);
            o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), headSize);
            o += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), (uint)packedSize); // ADD_SIZE
            o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), (uint)uncompressedSize); // UNP_SIZE
            o += 4;
            body[o++] = 2; // HostOS Unix
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0); // FileCRC
            o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0); // FileTime
            o += 4;
            body[o++] = 20; // UnpVer
            body[o++] = 0x30; // store
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), (ushort)nameBytes.Length);
            o += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0); // Attr
            o += 4;
            nameBytes.CopyTo(body.AsSpan(o));
            WriteHeader(ms, body);
        }

        var payload = new byte[packedSize];
        payloadPrefix.CopyTo(payload);
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

    /// <summary>
    /// Serves raw decoded bytes via CachedYencStream so tests do not depend on
    /// rapidyenc native (same approach as LazyRarResolverTests).
    /// </summary>
    private sealed class MemoryServingNntpClient(IReadOnlyDictionary<string, byte[]> segments) : NntpClient
    {
        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = segmentId.ToString();
            if (!segments.TryGetValue(key, out var bytes))
                throw new NzbWebDAV.Exceptions.UsenetArticleNotFoundException(key);

            var headers = new UsenetYencHeader
            {
                FileName = "vol.bin",
                FileSize = bytes.Length,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartOffset = 0,
                PartSize = bytes.Length,
            };
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = key,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body",
                Stream = new CachedYencStream(headers, new MemoryStream(bytes, writable: false)),
            });
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var responses = segmentIds
                .Select(id => DecodedBodyAsync(id, cancellationToken))
                .ToArray();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
