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
using static NzbWebDAV.Tests.Fakes.Rar4TestArchiveBuilder;

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
                        IsSplitAfter = true,
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
                ExpectedFileSize = 90 + packedSize,
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
        var volumeBytes = BuildRar4ContinuationVolume(pathInArchive, packedSize, trailingBytes: 12);
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
                            IsSplitAfter = true,
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
                    ExpectedFileSize = 90 + packedSize,
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
            Assert.Equal(volumeBytes.Length, meta.FileParts[1].SegmentIdByteRange.Count);

            for (var i = 0; i < 100 && reconciledSize is null; i++)
                await Task.Delay(20);

            Assert.True(reconcileSawPersistedBlob);
            Assert.Equal(mpf.Id, reconciledBlobId);
            Assert.Equal(90 + packedSize, reconciledSize);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", previous);
            try { Directory.Delete(configRoot, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task EnsureResolvedThroughAsync_TerminalPartDropsUnrelatedTrailingVolumes()
    {
        const string pathInArchive = "movie.mkv";
        var finalMemberBytes = BuildRar4ContinuationVolume(pathInArchive, packedSize: 800);
        var extraBytes = BuildRar4Volume(
            "extra.srt",
            packedSize: 100,
            splitBefore: false,
            splitAfter: false);
        var volumes = new Dictionary<string, byte[]>
        {
            ["vol2-seg0"] = finalMemberBytes,
            ["vol3-seg0"] = extraBytes,
        };
        using var client = new MeasuringNntpClient("unused", 0);
        var resolver = new LazyRarResolver(client, new ConfigManager())
        {
            VolumeStreamFactory = (ids, size) => new BoundedLengthStream(volumes[ids[0]], size),
        };
        var mpf = MultipartFile(
            pathInArchive,
            Pending("vol2-seg0", finalMemberBytes.Length, 780),
            Pending("vol3-seg0", extraBytes.Length, 20));

        var meta = await resolver.EnsureResolvedThroughAsync(
            mpf, long.MaxValue, CancellationToken.None);

        Assert.False(meta.IsLazy);
        Assert.Empty(meta.PendingParts);
        Assert.Equal(2, meta.FileParts.Length);
        Assert.Equal(800, meta.FileParts[1].FilePartByteRange.Count);
    }

    [Fact]
    public async Task EnsureResolvedThroughAsync_CoveredRangeSkipsLegacySplitRecovery()
    {
        using var client = new MeasuringNntpClient("unused", 0);
        var resolver = new LazyRarResolver(client, new ConfigManager())
        {
            VolumeStreamFactory = (_, _) => throw new InvalidOperationException(
                "Covered reads must not open a RAR volume."),
        };
        var mpf = MultipartFile(
            "movie.mkv",
            Pending("vol2-seg0", volumeLength: 800, estimatedDataSize: 800));
        mpf.Metadata.FileParts[0].IsSplitAfter = null;

        var meta = await resolver.EnsureResolvedThroughAsync(
            mpf, targetByteOffset: 0, ct: CancellationToken.None);

        Assert.Same(mpf.Metadata, meta);
        Assert.True(meta.IsLazy);
        Assert.Null(meta.FileParts[0].IsSplitAfter);
    }

    [Fact]
    public async Task EnsureResolvedThroughAsync_LegacyResolvedTerminalDropsPendingTail()
    {
        const string pathInArchive = "movie.mkv";
        var finalMemberBytes = BuildRar4ContinuationVolume(pathInArchive, packedSize: 800);
        var volumes = new Dictionary<string, byte[]>
        {
            ["vol2-seg0"] = finalMemberBytes,
        };
        using var client = new MeasuringNntpClient("unused", 0);
        var resolver = new LazyRarResolver(client, new ConfigManager())
        {
            VolumeStreamFactory = (ids, size) => new BoundedLengthStream(volumes[ids[0]], size),
        };
        var mpf = MultipartFile(
            pathInArchive,
            Pending("vol3-seg0", volumeLength: 200, estimatedDataSize: 20));
        mpf.Metadata.FileParts =
        [
            mpf.Metadata.FileParts[0],
            new DavMultipartFile.FilePart
            {
                SegmentIds = ["vol2-seg0"],
                SegmentIdByteRange = LongRange.FromStartAndSize(0, finalMemberBytes.Length),
                FilePartByteRange = LongRange.FromStartAndSize(
                    finalMemberBytes.Length - 800,
                    800),
                IsSplitAfter = null,
            }
        ];
        mpf.Metadata.ExpectedFileSize = 1_740;

        var meta = await resolver.EnsureResolvedThroughAsync(
            mpf, long.MaxValue, CancellationToken.None);

        Assert.False(meta.IsLazy);
        Assert.Empty(meta.PendingParts);
        Assert.Equal(2, meta.FileParts.Length);
        Assert.Equal(false, meta.FileParts[1].IsSplitAfter);
    }

    [Fact]
    public async Task EnsureResolvedThroughAsync_TerminalSizeMismatchDoesNotDropTail()
    {
        const string pathInArchive = "movie.mkv";
        var finalMemberBytes = BuildRar4ContinuationVolume(pathInArchive, packedSize: 800);
        var extraBytes = BuildRar4Volume(
            "extra.srt",
            packedSize: 100,
            splitBefore: false,
            splitAfter: false);
        var volumes = new Dictionary<string, byte[]>
        {
            ["vol2-seg0"] = finalMemberBytes,
            ["vol3-seg0"] = extraBytes,
        };
        using var client = new MeasuringNntpClient("unused", 0);
        var resolver = new LazyRarResolver(client, new ConfigManager())
        {
            VolumeStreamFactory = (ids, size) => new BoundedLengthStream(volumes[ids[0]], size),
        };
        var mpf = MultipartFile(
            pathInArchive,
            Pending("vol2-seg0", finalMemberBytes.Length, 780),
            Pending("vol3-seg0", extraBytes.Length, 20));
        mpf.Metadata.ExpectedFileSize = 2_000;

        var failure = await Assert.ThrowsAsync<NzbWebDAV.Exceptions.CorruptRarException>(
            () => resolver.EnsureResolvedThroughAsync(
                mpf, long.MaxValue, CancellationToken.None));

        Assert.Contains("resolves 1740 stored bytes, expected 2000", failure.Message);
        Assert.True(mpf.Metadata.IsLazy);
        Assert.Equal(2, mpf.Metadata.PendingParts.Length);
    }

    [Fact]
    public async Task EnsureResolvedThroughAsync_ChainContinuesPastLastVolume_Throws()
    {
        const string pathInArchive = "movie.mkv";
        var continuationBytes = BuildRar4ContinuationVolume(
            pathInArchive, packedSize: 800, splitAfter: true);
        using var client = new MeasuringNntpClient("unused", 0);
        var resolver = new LazyRarResolver(client, new ConfigManager())
        {
            VolumeStreamFactory = (_, size) => new BoundedLengthStream(continuationBytes, size),
        };
        var mpf = MultipartFile(
            pathInArchive,
            Pending("vol2-seg0", continuationBytes.Length, 800));

        var failure = await Assert.ThrowsAsync<NzbWebDAV.Exceptions.CorruptRarException>(
            () => resolver.EnsureResolvedThroughAsync(
                mpf, long.MaxValue, CancellationToken.None));

        Assert.Contains("continues beyond the final available volume", failure.Message);
        Assert.True(mpf.Metadata.IsLazy);
        Assert.Single(mpf.Metadata.PendingParts);
    }

    [Fact]
    public async Task EnsureResolvedThroughAsync_MismatchedContinuationReportsVolumeAndHeader()
    {
        var wrongMemberBytes = BuildRar4Volume(
            "extra.srt",
            packedSize: 100,
            splitBefore: true,
            splitAfter: false);
        using var client = new MeasuringNntpClient("unused", 0);
        var resolver = new LazyRarResolver(client, new ConfigManager())
        {
            VolumeStreamFactory = (_, size) => new BoundedLengthStream(wrongMemberBytes, size),
        };
        var mpf = MultipartFile(
            "movie.mkv",
            Pending("vol2-seg0", wrongMemberBytes.Length, 100));

        var failure = await Assert.ThrowsAsync<NzbWebDAV.Exceptions.CorruptRarException>(
            () => resolver.EnsureResolvedThroughAsync(
                mpf, long.MaxValue, CancellationToken.None));

        Assert.Contains("volume 2 of 2", failure.Message);
        Assert.Contains("'extra.srt'", failure.Message);
        Assert.Contains("split-before: True", failure.Message);
    }

    private static DavMultipartFile MultipartFile(
        string pathInArchive,
        params DavMultipartFile.PendingPart[] pending) =>
        new()
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
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, 1_000),
                        FilePartByteRange = LongRange.FromStartAndSize(60, 940),
                        IsSplitAfter = true,
                    }
                ],
                PendingParts = pending,
                ExpectedFileSize = 940 + pending.Sum(part => part.EstimatedDataSize),
            }
        };

    private static DavMultipartFile.PendingPart Pending(
        string segmentId, long volumeLength, long estimatedDataSize) =>
        new()
        {
            SegmentIds = [segmentId],
            SegmentIdByteRange = LongRange.FromStartAndSize(0, volumeLength),
            EstimatedDataSize = estimatedDataSize,
        };

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
