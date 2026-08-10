using System.Buffers.Binary;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Utils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public class LazyRarResolverTests
{
    [Fact]
    public async Task EnsureResolvedThroughAsync_SucceedsWithoutMeasure_WhenPendingEstimateIsShort()
    {
        const string pathInArchive = "movie.mkv";
        const int packedSize = 1000;
        var volumeBytes = BuildRar4ContinuationVolume(pathInArchive, packedSize);
        var trueSize = volumeBytes.Length;
        var underestimatedSize = (long)(trueSize * 0.95);
        Assert.True(underestimatedSize < trueSize);

        // Deferred data-skip means find-and-stop no longer seeks past packed
        // payload, so an understated Length alone does not fail header parse.
        await using (var shortStream = new BoundedLengthStream(volumeBytes, underestimatedSize))
        {
            var match = await RarUtil.FindFirstFileHeaderAsync(
                shortStream,
                password: null,
                h => h.FileName == pathInArchive,
                CancellationToken.None);
            Assert.NotNull(match);
            Assert.Equal(pathInArchive, match!.FileName);
            Assert.Equal(packedSize, match.AdditionalDataSize);
        }

        const string segmentId = "vol2-seg0";
        var client = new MeasuringNntpClient(segmentId, trueSize);
        var resolver = new LazyRarResolver(client, new ConfigManager())
        {
            // Bypass NzbFileStream/yEnc (rapidyenc native is not available on
            // all local RID targets). Still exercises resolution with an
            // understated Length estimate.
            VolumeStreamFactory = (_, size) => new BoundedLengthStream(volumeBytes, size),
        };

        var mpf = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                IsLazy = true,
                PathInArchive = pathInArchive,
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["vol1-seg0"],
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, 100),
                        FilePartByteRange = LongRange.FromStartAndSize(10, 90),
                    }
                ],
                PendingParts =
                [
                    new DavMultipartFile.PendingPart
                    {
                        SegmentIds = [segmentId],
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, underestimatedSize),
                        EstimatedDataSize = underestimatedSize - 80,
                    }
                ],
            }
        };

        var meta = await resolver.EnsureResolvedThroughAsync(mpf, long.MaxValue, CancellationToken.None);

        Assert.False(meta.IsLazy);
        Assert.Empty(meta.PendingParts);
        Assert.Equal(2, meta.FileParts.Length);
        var resolved = meta.FileParts[1];
        Assert.Equal([segmentId], resolved.SegmentIds);
        Assert.Equal(packedSize, resolved.FilePartByteRange.Count);
        Assert.Equal(resolved.FilePartByteRange.StartInclusive + packedSize,
            resolved.SegmentIdByteRange.Count);
        Assert.Equal(0, client.MeasuredSizeRequests);
    }

    [Fact]
    public async Task EnsureResolvedThroughAsync_ReconcilesFileSizeAfterBlobPersist()
    {
        const string pathInArchive = "movie.mkv";
        const int packedSize = 1000;
        var volumeBytes = BuildRar4ContinuationVolume(pathInArchive, packedSize);
        const string segmentId = "vol2-seg0";
        var client = new MeasuringNntpClient(segmentId, volumeBytes.Length);
        Guid? reconciledBlobId = null;
        long? reconciledSize = null;
        var reconcileSawPersistedBlob = false;

        var previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        var configRoot = Path.Join(Path.GetTempPath(), $"nzbdav-lazy-reconcile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", configRoot);
        try
        {
            var mpf = new DavMultipartFile
            {
                Id = Guid.NewGuid(),
                Metadata = new DavMultipartFile.Meta
                {
                    IsLazy = true,
                    PathInArchive = pathInArchive,
                    FileParts =
                    [
                        new DavMultipartFile.FilePart
                        {
                            SegmentIds = ["vol1-seg0"],
                            SegmentIdByteRange = LongRange.FromStartAndSize(0, 100),
                            FilePartByteRange = LongRange.FromStartAndSize(10, 90),
                        }
                    ],
                    PendingParts =
                    [
                        new DavMultipartFile.PendingPart
                        {
                            SegmentIds = [segmentId],
                            SegmentIdByteRange = LongRange.FromStartAndSize(0, volumeBytes.Length),
                            EstimatedDataSize = packedSize,
                        }
                    ],
                }
            };

            var resolver = new LazyRarResolver(client, new ConfigManager())
            {
                VolumeStreamFactory = (_, size) => new BoundedLengthStream(volumeBytes, size),
                ReconcileFileSizeAsync = (blobId, meta, _) =>
                {
                    reconcileSawPersistedBlob =
                        BlobStore.ReadBlob<DavMultipartFile>(blobId) is not null;
                    reconciledBlobId = blobId;
                    reconciledSize = MultipartFileSizeReconciler.TryGetPublishedSize(meta);
                    return Task.CompletedTask;
                },
            };

            var meta = await resolver.EnsureResolvedThroughAsync(mpf, long.MaxValue, CancellationToken.None);
            Assert.False(meta.IsLazy);

            for (var i = 0; i < 100 && reconciledSize is null; i++)
                await Task.Delay(20);

            Assert.True(reconcileSawPersistedBlob);
            Assert.Equal(mpf.Id, reconciledBlobId);
            Assert.Equal(90 + packedSize, reconciledSize);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", previous);
            try { Directory.Delete(configRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    // Minimal RAR4 multi-volume continuation: mark + archive(VOLUME) +
    // stored file header (HAS_DATA|SPLIT_BEFORE) + packed payload.
    private static byte[] BuildRar4ContinuationVolume(string fileName, int packedSize)
    {
        using var ms = new MemoryStream();
        ms.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        // Archive header (HEAD_SIZE=13 including CRC).
        {
            Span<byte> body = stackalloc byte[11];
            body[0] = 0x73;
            BinaryPrimitives.WriteUInt16LittleEndian(body[1..], 0x0001); // MHD_VOLUME
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
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), 0x8001); // HAS_DATA|SPLIT_BEFORE
            o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), headSize);
            o += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), (uint)packedSize); // ADD_SIZE
            o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), (uint)packedSize); // UNP_SIZE
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

        ms.Write(new byte[packedSize]);
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

    // Only GetYencHeadersAsync is used by the measured-size retry path.
    private sealed class MeasuringNntpClient(string segmentId, long measuredSize) : NntpClient
    {
        public int MeasuredSizeRequests { get; private set; }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId id,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId id,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string id, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId id,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId id,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetYencHeader> GetYencHeadersAsync(
            string id, CancellationToken ct)
        {
            MeasuredSizeRequests++;
            Assert.Equal(segmentId, id);
            return Task.FromResult(new UsenetYencHeader
            {
                FileName = "volume.rar",
                FileSize = measuredSize,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartOffset = 0,
                PartSize = measuredSize,
            });
        }

        public override void Dispose()
        {
        }
    }

    // Mirrors NzbFileStream's strict Length check so understated Length
    // fails the SharpCompress data-end seek the same way production does.
    private sealed class BoundedLengthStream(byte[] data, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > length)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "Seek position is outside stream bounds.");
                }

                _position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= length) return 0;
            var n = (int)Math.Min(count, length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var absolute = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = absolute;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
