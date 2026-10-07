using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using MemoryPack;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(ConfigPathCollection))]
public class DavMultipartFileStreamTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_EmptyPersistedMetadata_ReturnsEofWithoutNntpRequests(
        bool usePipelinedBodyRequests)
    {
        var metadata = new DavMultipartFile.Meta
        {
            AesParams = null,
            FileParts = [],
        };
        var serialized = MemoryPackSerializer.Serialize(metadata);
        var restored = MemoryPackSerializer.Deserialize<DavMultipartFile.Meta>(serialized);
        Assert.NotNull(restored);
        Assert.Empty(restored.FileParts);
        Assert.Null(restored.AesParams);
        Assert.False(restored.IsLazy);

        using var client = new FakeNntpClient(
            new Dictionary<string, byte[]>(), useCachedYencStreams: true);
        await using var stream = new DavMultipartFileStream(
            new DavMultipartFile { Id = Guid.NewGuid(), Metadata = restored },
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: usePipelinedBodyRequests,
            fileName: "empty.txt");

        Assert.Equal(0L, stream.Length);
        Assert.Equal(0L, stream.Position);
        Assert.Equal(0, await stream.ReadAsync(new byte[8]));
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.Equal(0L, stream.Seek(0, SeekOrigin.Begin));
        Assert.Equal(0L, stream.Seek(0, SeekOrigin.End));
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
        Assert.Equal(0L, stream.Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(1, SeekOrigin.Begin));
        Assert.Equal(0, client.BodyRequestCount);
        Assert.Equal(0, client.BatchRequestCount);
        Assert.Equal(0, client.HeaderProbeCount);
        Assert.Empty(client.RequestedSegmentIds);
    }

    [Fact]
    public async Task ReadAsync_ProofBackedPendingPartsAreNotEagerlyOpened()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = [0, 1, 2, 3, 4, 5, 6, 7],
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(new LongRange(0, 8), new LongRange(0, 8));
        multipart.Metadata.IsLazy = true;
        multipart.Metadata.PathInArchive = "movie.mkv";
        multipart.Metadata.FileParts[0].IsSplitAfter = true;
        multipart.Metadata.PendingParts =
        [
            new DavMultipartFile.PendingPart
            {
                SegmentIds = ["pending"],
                SegmentIdByteRange = new LongRange(0, 8),
                EstimatedDataSize = 8,
                VerificationProof = new Par2FileProof(),
            }
        ];
        var opened = 0;
        var resolver = new LazyRarResolver(client, new ConfigManager())
        {
            VolumeStreamFactory = (_, _) =>
            {
                Interlocked.Increment(ref opened);
                return new MemoryStream();
            },
        };
        await using var stream = new DavMultipartFileStream(
            multipart, client, 0, resolver, usePipelinedBodyRequests: false);

        Assert.Equal(8, await stream.ReadAsync(new byte[8]));
        Assert.Equal(0, Volatile.Read(ref opened));
    }

    [Fact]
    public void GetEffectivePartLength_UsesPackedRangeEndForUnderestimatedVolume()
    {
        var part = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(4, 12))
            .Metadata.FileParts[0];

        Assert.Equal(16, DavMultipartFileStream.GetEffectivePartLength(part));
    }

    [Fact]
    public async Task ReadAsync_HealsUnderestimatedVolumeLength()
    {
        var volumeBytes = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = volumeBytes,
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(4, 12));
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var buffer = new byte[12];
        var bytesRead = await stream.ReadAsync(buffer);

        Assert.Equal(buffer.Length, bytesRead);
        Assert.Equal(volumeBytes[4..], buffer);
    }

    [Fact]
    public async Task ReadAsync_FiniteRangeEndingBeforeMultipartLengthReturnsEof()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["one"] = [0, 1, 2, 3, 4, 5, 6, 7],
            ["two"] = [8, 9, 10, 11, 12, 13, 14, 15],
        }, useCachedYencStreams: true);
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["one"],
                        SegmentIdByteRange = new LongRange(0, 8),
                        FilePartByteRange = new LongRange(0, 8),
                    },
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["two"],
                        SegmentIdByteRange = new LongRange(0, 8),
                        FilePartByteRange = new LongRange(8, 16),
                    },
                ],
            },
        };
        using var requestCts = new CancellationTokenSource();
        using var schedulingScope = requestCts.Token.SetContext(new StreamingSchedulingContext
        {
            Snapshot = new StreamingCapacitySnapshot(
                IsPerStreamMode: false,
                ConfiguredDownloadBudget: 20,
                ConfiguredPerStreamBudget: 15,
                ActiveReaderShareCount: 1,
                EffectivePrimaryTransferCapacity: 20,
                EffectiveStreamConnectionTarget: 20,
                ArticleBufferSize: 4,
                InFlightArticleBudgetBytes: 1024,
                Reason: StreamingCapacityReason.Ok),
        });
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(5);
        try
        {
            await using var stream = new DavMultipartFileStream(
                multipart,
                client,
                articleBufferSize: 4,
                resolver: null,
                usePipelinedBodyRequests: true,
                fileName: "movie.mkv");
            var buffer = new byte[5];

            Assert.Equal(5, await stream.ReadAsync(buffer, requestCts.Token));
            Assert.Equal([0, 1, 2, 3, 4], buffer);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], requestCts.Token));
            await stream.DisposeAsync();
            Assert.False(client.BodyRequestCounts.ContainsKey("two"));
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }
    }

    [Fact]
    public async Task ReadAsync_StartsNextVolumeBodyRequestBeforeCurrentVolumeEof()
    {
        var nextBodyRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = [0, 1, 2, 3, 4, 5, 6, 7],
                ["two"] = [8, 9, 10, 11, 12, 13, 14, 15],
            },
            useCachedYencStreams: true,
            decodedStreamFactory: (id, bytes) =>
            {
                if (id == "two") nextBodyRequested.TrySetResult();
                return new MemoryStream(bytes, writable: false);
            });
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["one"],
                        SegmentIdByteRange = new LongRange(0, 8),
                        FilePartByteRange = new LongRange(0, 8),
                    },
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["two"],
                        SegmentIdByteRange = new LongRange(0, 8),
                        FilePartByteRange = new LongRange(0, 8),
                    },
                ],
            },
        };
        await using var stream = new DavMultipartFileStream(
            multipart, client, articleBufferSize: 4, resolver: null,
            usePipelinedBodyRequests: false, fileName: "movie.mkv");

        Assert.Equal(2, await stream.ReadAsync(new byte[2]));
        await nextBodyRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, stream.Position);

        using var rest = new MemoryStream();
        await stream.CopyToAsync(rest);
        Assert.Equal(Enumerable.Range(2, 14).Select(x => (byte)x).ToArray(), rest.ToArray());
    }

    [Fact]
    public async Task ReadAsync_ReadAheadWindowSpansMoreThanOneUpcomingVolume()
    {
        // Four 8-byte volumes against a 32-byte read-ahead window: while the reader is
        // still in the first volume, the window covers the third one too.
        var thirdBodyRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var names = new[] { "one", "two", "three", "four" };
        using var client = new FakeNntpClient(
            names.Select((name, part) => (name, part)).ToDictionary(
                x => x.name,
                x => Enumerable.Range(x.part * 8, 8).Select(b => (byte)b).ToArray()),
            useCachedYencStreams: true,
            decodedStreamFactory: (id, bytes) =>
            {
                if (id == "three") thirdBodyRequested.TrySetResult();
                return new MemoryStream(bytes, writable: false);
            });
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts = names.Select(name => new DavMultipartFile.FilePart
                {
                    SegmentIds = [name],
                    SegmentIdByteRange = new LongRange(0, 8),
                    FilePartByteRange = new LongRange(0, 8),
                }).ToArray(),
            },
        };
        await using var stream = new DavMultipartFileStream(
            multipart, client, articleBufferSize: 4, resolver: null,
            usePipelinedBodyRequests: false, fileName: "movie.mkv");

        var buffer = new byte[1];
        while (stream.Position < 7 && !thirdBodyRequested.Task.IsCompleted)
        {
            Assert.Equal(1, await stream.ReadAsync(buffer));
            await Task.WhenAny(thirdBodyRequested.Task, Task.Delay(500));
        }

        Assert.True(thirdBodyRequested.Task.IsCompleted);
        Assert.True(stream.Position < 8);
        using var rest = new MemoryStream();
        await stream.CopyToAsync(rest);
        Assert.Equal(
            Enumerable.Range((int)(32 - rest.Length), (int)rest.Length).Select(x => (byte)x).ToArray(),
            rest.ToArray());
        Assert.Equal(32, stream.Position);
    }

    [Theory]
    [InlineData(0, true)]
    // Unindexed volume with an archive header: the inner stream opens via fast seek.
    [InlineData(3, false)]
    public async Task ReadAsync_NextVolumePrefetchUnderTightBudgetCompletesAndReleasesLeases(
        int headerBytes, bool indexed)
    {
        const int segmentsPerPart = 4;
        const int segmentSize = 8;
        const int volumeSize = segmentsPerPart * segmentSize;
        var segments = new Dictionary<string, byte[]>();
        var segmentRanges = new Dictionary<string, LongRange>();
        DavMultipartFile.FilePart Part(string name, int partIndex)
        {
            var ids = Enumerable.Range(0, segmentsPerPart).Select(i => $"{name}-{i}").ToArray();
            for (var i = 0; i < segmentsPerPart; i++)
            {
                var first = partIndex * volumeSize + i * segmentSize;
                segments[ids[i]] = Enumerable.Range(first, segmentSize).Select(x => (byte)x).ToArray();
                segmentRanges[ids[i]] = LongRange.FromStartAndSize(i * segmentSize, segmentSize);
            }
            return new DavMultipartFile.FilePart
            {
                SegmentIds = ids,
                SegmentIdByteRange = new LongRange(0, volumeSize),
                FilePartByteRange = new LongRange(headerBytes, volumeSize),
                SegmentByteRanges = indexed
                    ? ids.Select(id => segmentRanges[id]).ToArray()
                    : null,
                SegmentByteRangesTrusted = indexed,
            };
        }
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta { FileParts = [Part("one", 0), Part("two", 1)] },
        };
        using var client = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: segmentRanges);
        // One segment of credit: any lease held by the next volume would starve the current tail.
        var budget = new InFlightArticleBudget(segmentSize);
        var stream = new DavMultipartFileStream(
            multipart, client, articleBufferSize: 4, resolver: null,
            usePipelinedBodyRequests: false, fileName: "movie.mkv", inFlightArticleBudget: budget);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var all = new MemoryStream();
        var buffer = new byte[2];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            all.Write(buffer, 0, read);
        await stream.DisposeAsync();

        Assert.Equal(
            Enumerable.Range(0, 2 * volumeSize)
                .Where(x => x % volumeSize >= headerBytes)
                .Select(x => (byte)x).ToArray(),
            all.ToArray());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (budget.LeasedBytes != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(4, true)]
    public async Task ReadAsync_InferredGeometryContradictedByArticles_StreamsExactBytes(
        int articleBufferSize, bool withProof)
    {
        var segments = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, LongRange>(StringComparer.Ordinal);
        var next = 0;
        var first = AddVolume(segments, ranges, "a", [8], ref next);
        // Second and last headers fit 100-byte uniform segments; the middle ones do not.
        var second = AddVolume(segments, ranges, "b", [100, 100, 60, 140, 80], ref next);
        if (withProof) second.VerificationProof = VolumeProof(segments, second);
        using var client = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta { PathInArchive = "movie.mkv", FileParts = [first, second] },
        };
        await using var stream = new DavMultipartFileStream(
            multipart, client, articleBufferSize, new LazyRarResolver(client, new ConfigManager()),
            usePipelinedBodyRequests: articleBufferSize > 0, fileName: "movie.mkv");

        var bytes = await ReadFullyAsync(stream, next);

        Assert.Equal(Enumerable.Range(0, next).Select(x => (byte)x).ToArray(), bytes);
        Assert.True(client.HeaderProbeCount > 0);
        Assert.NotEqual(true, multipart.Metadata.FileParts[1].SegmentByteRangesTrusted);
    }

    [Fact]
    public async Task ReadAsync_FiniteRange_ProbesGeometryOnlyForVolumesItStreams()
    {
        var segments = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, LongRange>(StringComparer.Ordinal);
        var next = 0;
        var parts = new[] { "a", "b", "c", "d" }
            .Select(name => AddVolume(segments, ranges, name, [8, 8, 8, 8], ref next))
            .ToArray();
        var probed = new List<string>();
        using var client = new FakeNntpClient(
            segments, useCachedYencStreams: true, segmentRanges: ranges,
            headerProbeFailure: (id, _) =>
            {
                lock (probed) probed.Add(id);
                return null;
            });
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta { PathInArchive = "movie.mkv", FileParts = parts },
        };
        using var requestCts = new CancellationTokenSource();
        using var schedulingScope = requestCts.Token.SetContext(new StreamingSchedulingContext
        {
            Snapshot = new StreamingCapacitySnapshot(
                IsPerStreamMode: false,
                ConfiguredDownloadBudget: 20,
                ConfiguredPerStreamBudget: 15,
                ActiveReaderShareCount: 1,
                EffectivePrimaryTransferCapacity: 20,
                EffectiveStreamConnectionTarget: 20,
                ArticleBufferSize: 4,
                InFlightArticleBudgetBytes: 1024,
                Reason: StreamingCapacityReason.Ok),
        });
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        // Volumes a and b, plus one byte of c.
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(65);
        try
        {
            await using var stream = new DavMultipartFileStream(
                multipart, client, articleBufferSize: 4, new LazyRarResolver(client, new ConfigManager()),
                usePipelinedBodyRequests: true, fileName: "movie.mkv");
            using var all = new MemoryStream();
            var buffer = new byte[16];
            int read;
            while ((read = await stream.ReadAsync(buffer, requestCts.Token)) > 0)
                all.Write(buffer, 0, read);

            Assert.Equal(Enumerable.Range(0, 65).Select(x => (byte)x).ToArray(), all.ToArray());
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }

        // The first volume boundary is prepared; the one-byte tail and unread volumes are not.
        Assert.NotEmpty(probed);
        Assert.All(probed, id => Assert.StartsWith("b-", id, StringComparison.Ordinal));
        Assert.True(multipart.Metadata.FileParts[1].SegmentByteRangesTrusted);
        Assert.DoesNotContain(client.BodyRequestCounts.Keys, id => id.StartsWith("d-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Seek_IntoContradictedInferredVolume_StreamsExactBytes(int articleBufferSize)
    {
        var segments = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, LongRange>(StringComparer.Ordinal);
        var next = 0;
        var first = AddVolume(segments, ranges, "a", [8], ref next);
        var second = AddVolume(segments, ranges, "b", [100, 100, 60, 140, 80], ref next, headerBytes: 10);
        using var client = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta { PathInArchive = "movie.mkv", FileParts = [first, second] },
        };
        var resolver = new LazyRarResolver(client, new ConfigManager());
        await resolver.PrepareSegmentGeometryAsync(multipart, 1, CancellationToken.None);
        Assert.True(multipart.Metadata.FileParts[1].SegmentByteRangesTrusted);
        await using var stream = new DavMultipartFileStream(
            multipart, client, articleBufferSize, resolver,
            usePipelinedBodyRequests: articleBufferSize > 0, fileName: "movie.mkv");

        // Volume offset 270 lies in b-3; the inferred uniform ranges place it in b-2.
        const int target = 8 + 270 - 10;
        stream.Seek(target, SeekOrigin.Begin);
        var bytes = await ReadFullyAsync(stream, 50);

        Assert.Equal(Enumerable.Range(target, 50).Select(x => (byte)x).ToArray(), bytes);
        Assert.NotEqual(true, multipart.Metadata.FileParts[1].SegmentByteRangesTrusted);
    }

    [Fact]
    public async Task ReadAsync_GeometryRecoveryAfterPartialDelivery_KeepsTheOriginalRangeEnd()
    {
        var segments = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, LongRange>(StringComparer.Ordinal);
        var next = 0;
        var first = AddVolume(segments, ranges, "a", [8], ref next);
        // b-2 contradicts the inferred 100-byte segments after 208 bytes were delivered.
        var second = AddVolume(segments, ranges, "b", [100, 100, 60, 140, 80], ref next);
        using var client = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta { PathInArchive = "movie.mkv", FileParts = [first, second] },
        };
        using var requestCts = new CancellationTokenSource();
        using var schedulingScope = requestCts.Token.SetContext(new StreamingSchedulingContext
        {
            Snapshot = new StreamingCapacitySnapshot(
                IsPerStreamMode: false,
                ConfiguredDownloadBudget: 20,
                ConfiguredPerStreamBudget: 15,
                ActiveReaderShareCount: 1,
                EffectivePrimaryTransferCapacity: 20,
                EffectiveStreamConnectionTarget: 20,
                ArticleBufferSize: 4,
                InFlightArticleBudgetBytes: 1024,
                Reason: StreamingCapacityReason.Ok),
        });
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(300);
        try
        {
            await using var stream = new DavMultipartFileStream(
                multipart, client, articleBufferSize: 4, new LazyRarResolver(client, new ConfigManager()),
                usePipelinedBodyRequests: true, fileName: "movie.mkv");
            using var all = new MemoryStream();
            var buffer = new byte[16];
            int read;
            while ((read = await stream.ReadAsync(buffer, requestCts.Token)) > 0)
                all.Write(buffer, 0, read);

            Assert.Equal(Enumerable.Range(0, 300).Select(x => (byte)x).ToArray(), all.ToArray());
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }

        Assert.NotEqual(true, multipart.Metadata.FileParts[1].SegmentByteRangesTrusted);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(4, true)]
    public async Task ReadAsync_HealthyFallbackContradictingInferredSize_RecoversWithoutHoles(
        int articleBufferSize, bool withProof)
    {
        var segments = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, LongRange>(StringComparer.Ordinal);
        var next = 0;
        var first = AddVolume(segments, ranges, "a", [8], ref next);
        var second = AddVolume(segments, ranges, "b", [100, 100, 60, 140, 80], ref next);
        if (withProof) second.VerificationProof = VolumeProof(segments, second);
        // The b-2 primary is gone; its healthy alternate declares the real 60 bytes.
        segments["b-2-alt"] = segments["b-2"];
        ranges["b-2-alt"] = ranges["b-2"];
        segments.Remove("b-2");
        second.SegmentFallbackIds = [[], [], ["b-2-alt"], [], []];
        using var client = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta { PathInArchive = "movie.mkv", FileParts = [first, second] },
        };
        var resolver = new LazyRarResolver(client, new ConfigManager());
        await resolver.PrepareSegmentGeometryAsync(multipart, 1, CancellationToken.None);
        Assert.True(multipart.Metadata.FileParts[1].SegmentByteRangesTrusted);
        var path = $"/view/{Guid.NewGuid():N}.mkv";
        await using var stream = new DavMultipartFileStream(
            multipart, client, articleBufferSize, resolver,
            usePipelinedBodyRequests: articleBufferSize > 0, fileName: path);

        var bytes = await ReadFullyAsync(stream, next);

        Assert.Equal(Enumerable.Range(0, next).Select(x => (byte)x).ToArray(), bytes);
        Assert.Null(PlaybackHoleTracker.SnapshotMissingSegmentIds(path));
        Assert.NotEqual(true, multipart.Metadata.FileParts[1].SegmentByteRangesTrusted);
    }

    [Fact]
    public async Task ReadAsync_SecondReaderAfterSharedGeometryRejection_StillRecovers()
    {
        var segments = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, LongRange>(StringComparer.Ordinal);
        var next = 0;
        var first = AddVolume(segments, ranges, "a", [8], ref next);
        var second = AddVolume(segments, ranges, "b", [100, 100, 60, 140, 80], ref next);
        using var client = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta { PathInArchive = "movie.mkv", FileParts = [first, second] },
        };
        var resolver = new LazyRarResolver(client, new ConfigManager());
        await resolver.PrepareSegmentGeometryAsync(multipart, 1, CancellationToken.None);
        await using var readerA = new DavMultipartFileStream(
            multipart, client, articleBufferSize: 0, resolver, usePipelinedBodyRequests: false, fileName: "movie.mkv");
        await using var readerB = new DavMultipartFileStream(
            multipart, client, articleBufferSize: 0, resolver, usePipelinedBodyRequests: false, fileName: "movie.mkv");

        // Gate: both readers open volume b on the inferred ranges before either sees b-2.
        const int gate = 8 + 150;
        var headA = await ReadFullyAsync(readerA, gate);
        var headB = await ReadFullyAsync(readerB, gate);
        Assert.True(multipart.Metadata.FileParts[1].SegmentByteRangesTrusted);
        var tailA = await ReadFullyAsync(readerA, next - gate);
        Assert.NotEqual(true, multipart.Metadata.FileParts[1].SegmentByteRangesTrusted);
        var tailB = await ReadFullyAsync(readerB, next - gate);

        var expected = Enumerable.Range(0, next).Select(x => (byte)x).ToArray();
        Assert.Equal(expected, headA.Concat(tailA).ToArray());
        Assert.Equal(expected, headB.Concat(tailB).ToArray());
    }

    private static Par2FileProof VolumeProof(Dictionary<string, byte[]> segments, DavMultipartFile.FilePart part) =>
        Par2VerifiedFileStreamTests.CreateProof(part.SegmentIds.SelectMany(id => segments[id]).ToArray(), 64);

    private static DavMultipartFile.FilePart AddVolume(
        Dictionary<string, byte[]> segments,
        Dictionary<string, LongRange> ranges,
        string name,
        int[] sizes,
        ref int next,
        int headerBytes = 0)
    {
        var ids = new string[sizes.Length];
        var offset = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            ids[i] = $"{name}-{i}";
            var payloadStart = next - headerBytes;
            segments[ids[i]] = Enumerable.Range(offset, sizes[i])
                .Select(x => x < headerBytes ? (byte)0xEE : (byte)(payloadStart + x))
                .ToArray();
            ranges[ids[i]] = LongRange.FromStartAndSize(offset, sizes[i]);
            offset += sizes[i];
        }

        next += offset - headerBytes;
        return new DavMultipartFile.FilePart
        {
            SegmentIds = ids,
            SegmentIdByteRange = new LongRange(0, offset),
            FilePartByteRange = new LongRange(headerBytes, offset),
        };
    }

    [Fact]
    public async Task ReadAsync_TailOfPersistedLazyPartWithTrailingArchiveBytes_Succeeds()
    {
        var volumeBytes = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        using var client = new FakeNntpClient(
            new Dictionary<string, byte[]> { ["segment"] = volumeBytes },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange> { ["segment"] = new(0, 16) });
        // Mimic an already-persisted lazy part where the recorded packed-data
        // end excludes the trailing RAR structure in its final yEnc segment.
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 12),
            fileRange: LongRange.FromStartAndSize(4, 8));
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(1);
        try
        {
            await using var stream = new DavMultipartFileStream(
                multipart,
                client,
                articleBufferSize: 0,
                resolver: null,
                usePipelinedBodyRequests: false,
                fileName: "movie.mkv");
            stream.Seek(7, SeekOrigin.Begin);

            var buffer = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(buffer));
            Assert.Equal(11, buffer[0]);
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }
    }

    [Fact]
    public async Task ReadAsync_ExactIndexedOffsetDelegatesFirstByteBeforeContainingBodyEof()
    {
        var volumeOne = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray();
        var volumeTwo = Enumerable.Range(8, 8).Select(x => (byte)x).ToArray();
        var staged = new StagedBodyStream(
            prefix: volumeTwo[..2],
            requested: volumeTwo[2..3],
            tail: volumeTwo[3..]);
        using var client = new FakeNntpClient(
            new Dictionary<string, byte[]>
            {
                ["one"] = volumeOne,
                ["two"] = volumeTwo,
            },
            useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, LongRange>
            {
                ["one"] = new(0, 8),
                ["two"] = new(8, 16),
            },
            decodedStreamFactory: (id, bytes) =>
                id == "two" ? staged : new MemoryStream(bytes, writable: false));
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["one", "two"],
                        SegmentIdByteRange = new LongRange(0, 16),
                        FilePartByteRange = new LongRange(0, 16),
                        SegmentByteRanges = [new LongRange(0, 8), new LongRange(8, 16)],
                        SegmentByteRangesTrusted = true,
                    }
                ],
            },
        };
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(2L * 1024 * 1024);
        try
        {
            await using var stream = new DavMultipartFileStream(
                multipart,
                client,
                articleBufferSize: 4,
                resolver: null,
                usePipelinedBodyRequests: false,
                fileName: "movie.mkv");
            stream.Seek(10, SeekOrigin.Begin);

            var buffer = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(buffer));
            Assert.Equal(10, buffer[0]);
            Assert.True(staged.TailGateClosed);
            Assert.False(client.BodyRequestCounts.ContainsKey("one"));
            Assert.Equal(1, client.BodyRequestCounts["two"]);
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }
    }

    [Fact]
    public async Task ReadAsync_PersistedLazyPartFindsPenultimateSegmentBeforeTrailingArchiveBytes()
    {
        var segmentIds = new[] { "one", "two", "three" };
        var segments = segmentIds.ToDictionary(
            id => id,
            _ => Enumerable.Range(0, 10).Select(value => (byte)value).ToArray());
        var ranges = new[]
        {
            new LongRange(0, 10),
            new LongRange(10, 20),
            new LongRange(20, 30),
        };
        using var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            segmentRanges: segmentIds.Zip(ranges).ToDictionary(pair => pair.First, pair => pair.Second));
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = segmentIds,
                        SegmentIdByteRange = new LongRange(0, 24),
                        FilePartByteRange = new LongRange(0, 24),
                    }
                ],
            },
        };
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(1024 * 1024 - 1);
        try
        {
            await using var stream = new DavMultipartFileStream(
                multipart,
                client,
                articleBufferSize: 0,
                resolver: null,
                usePipelinedBodyRequests: false,
                fileName: "movie.mkv");
            stream.Seek(18, SeekOrigin.Begin);

            var buffer = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(buffer));
            Assert.Equal(8, buffer[0]);
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }
    }

    [Fact]
    public void Read_PreservesSynchronousArchiveParserCompatibility()
    {
        var volumeBytes = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = volumeBytes,
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(4, 12));
        using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var buffer = new byte[12];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, bytesRead);
        Assert.Equal(volumeBytes[4..], buffer);
    }

    [Fact]
    public async Task ReadAsync_InvalidSegmentRangeThrowsKnownSeekError()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>());
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(1, 8),
            fileRange: LongRange.FromStartAndSize(4, 4));
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        await Assert.ThrowsAsync<SeekPositionNotFoundException>(
            () => stream.ReadAsync(new byte[1], 0, 1));
    }

    [Fact]
    public async Task ReadAsync_UnencryptedShortVolume_FailsWithPartProvenance()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray(),
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 12),
            fileRange: LongRange.FromStartAndSize(0, 12));
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var failure = await Assert.ThrowsAsync<IncompleteMultipartPartException>(
            () => ReadFullyAsync(stream, 12));

        Assert.Contains("delivered 8 of 12 expected bytes", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Part 1 of 1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("encrypted: False", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_EncryptedVolumeShortByLessThanAnAesBlock_KeepsFollowingOffsets()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Enumerable.Range(1, 12).Select(x => (byte)x).ToArray(),
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 16),
            fileRange: LongRange.FromStartAndSize(0, 16));
        multipart.Metadata.AesParams = new AesParams();
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var bytes = await ReadFullyAsync(stream, 16);

        Assert.Equal(Enumerable.Range(1, 12).Select(x => (byte)x), bytes[..12]);
        Assert.Equal(new byte[4], bytes[12..]);
    }

    [Fact]
    public async Task ReadAsync_PendingPartsWithoutResolver_EndsBeforeDeclaredLength_ThrowsIncompleteFileContent()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray(),
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(0, 8));
        multipart.Metadata.PendingParts =
        [
            new DavMultipartFile.PendingPart
            {
                SegmentIds = ["vol2-seg"],
                SegmentIdByteRange = LongRange.FromStartAndSize(0, 8),
                EstimatedDataSize = 8,
            }
        ];
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var buffer = new byte[16];
        Assert.Equal(8, await stream.ReadAsync(buffer.AsMemory(0, 8)));

        var failure = await Assert.ThrowsAsync<IncompleteFileContentException>(async () =>
        {
            _ = await stream.ReadAsync(buffer.AsMemory(8));
        });

        Assert.Equal(16, failure.ExpectedBytes);
        Assert.Equal(8, failure.DeliveredBytes);
        Assert.Contains("movie.mkv", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_ExpectedFileSizeKeepsRecoveredLegacyLengthStable()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray(),
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(0, 8));
        multipart.Metadata.IsLazy = true;
        multipart.Metadata.ExpectedFileSize = 8;
        multipart.Metadata.PendingParts =
        [
            new DavMultipartFile.PendingPart
            {
                SegmentIds = ["unrelated-tail"],
                SegmentIdByteRange = LongRange.FromStartAndSize(0, 8),
                EstimatedDataSize = 8,
            }
        ];
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var buffer = new byte[8];
        Assert.Equal(8, await stream.ReadAsync(buffer));
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
        Assert.Equal(8, stream.Length);
    }

    [Fact]
    public async Task ReadAsync_UnresolvableTrailingVolume_DoesNotLookLikeEndOfFile()
    {
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Enumerable.Range(0, 8).Select(x => (byte)x).ToArray(),
        }, useCachedYencStreams: true);
        var multipart = MultipartFile(
            segmentRange: LongRange.FromStartAndSize(0, 8),
            fileRange: LongRange.FromStartAndSize(0, 8));
        multipart.Metadata.IsLazy = true;
        multipart.Metadata.PathInArchive = "movie.mkv";
        multipart.Metadata.PendingParts =
        [
            new DavMultipartFile.PendingPart
            {
                SegmentIds = ["vol2-seg0"],
                SegmentIdByteRange = LongRange.FromStartAndSize(0, 8),
                EstimatedDataSize = 8,
            }
        ];
        await using var stream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 0,
            resolver: new StalledRarResolver(client),
            usePipelinedBodyRequests: false,
            fileName: "movie.mkv");

        var failure = await Assert.ThrowsAsync<IncompleteMultipartPartException>(
            () => ReadFullyAsync(stream, 16));

        Assert.Contains("Volume 2 of \"movie.mkv\" could not be resolved",
            failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StripedPipelinedReads_StoredOrEncryptedVolumes_SeekToTheSameBytes(bool encrypted)
    {
        const int segmentSize = 64;
        const int segmentsPerVolume = 12;
        const int volumeSize = segmentSize * segmentsPerVolume;
        var plaintext = Enumerable.Range(0, 2 * volumeSize).Select(index => (byte)(index * 7 + 3)).ToArray();
        var (packed, aes) = encrypted ? Encrypt(plaintext) : (plaintext, null);
        var segments = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, LongRange>(StringComparer.Ordinal);
        var allIds = new List<string>();
        var parts = new List<DavMultipartFile.FilePart>();
        for (var volume = 0; volume < 2; volume++)
        {
            var ids = new string[segmentsPerVolume];
            for (var segment = 0; segment < segmentsPerVolume; segment++)
            {
                var id = ids[segment] = $"v{volume}-s{segment}";
                segments[id] = packed.AsSpan((volume * segmentsPerVolume + segment) * segmentSize, segmentSize).ToArray();
                ranges[id] = LongRange.FromStartAndSize(segment * segmentSize, segmentSize);
            }

            allIds.AddRange(ids);
            parts.Add(new DavMultipartFile.FilePart
            {
                SegmentIds = ids,
                SegmentIdByteRange = new LongRange(0, volumeSize),
                FilePartByteRange = new LongRange(0, volumeSize),
                SegmentByteRanges = [.. ids.Select(id => ranges[id])],
                SegmentByteRangesTrusted = true,
            });
        }

        using var client = new FakeNntpClient(segments, useCachedYencStreams: true, segmentRanges: ranges);
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta { FileParts = [.. parts], AesParams = aes },
        };
        using var cts = new CancellationTokenSource();
        using var hint = cts.Token.SetContext(new StreamingStripeContext { StripeCount = 4 });
        await using var packedStream = new DavMultipartFileStream(
            multipart,
            client,
            articleBufferSize: 8,
            resolver: null,
            usePipelinedBodyRequests: true,
            fileName: "movie.mkv");
        // Mirrors DatabaseStoreMultipartFile, which decrypts above the multipart stream.
        await using Stream stream = aes is null ? packedStream : new AesDecoderStream(packedStream, aes);

        foreach (var position in new[] { 0, 700, 1000, 100, 1400 })
        {
            stream.Seek(position, SeekOrigin.Begin);
            var expected = plaintext.AsSpan(position, Math.Min(200, plaintext.Length - position)).ToArray();
            var actual = new byte[expected.Length];
            await stream.ReadExactlyAsync(actual, cts.Token);
            Assert.Equal(expected, actual);
        }

        Assert.Contains(
            client.BatchSegmentIds,
            batch => batch.Length > 1 && allIds.IndexOf(batch[1]) != allIds.IndexOf(batch[0]) + 1);
    }

    private static (byte[] Ciphertext, AesParams Parameters) Encrypt(byte[] plaintext)
    {
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var iv = Enumerable.Range(32, 16).Select(index => (byte)index).ToArray();
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(key, iv);
        var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        return (ciphertext, new AesParams { Key = key, Iv = iv, DecodedSize = plaintext.Length });
    }

    private static async Task<byte[]> ReadFullyAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0) break;
            offset += read;
        }

        Assert.Equal(count, offset);
        return buffer;
    }

    // Stands in for a resolver that returns without materializing the volume it was
    // asked for — the case where the archive layout could not be read.
    private sealed class StalledRarResolver(INntpClient client)
        : LazyRarResolver(client, new ConfigManager())
    {
        public override Task<DavMultipartFile.Meta> ResolveNextAsync(
            DavMultipartFile mpf, CancellationToken ct) =>
            Task.FromResult(mpf.Metadata);
    }

    private static DavMultipartFile MultipartFile(LongRange segmentRange, LongRange fileRange) =>
        new()
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["segment"],
                        SegmentIdByteRange = segmentRange,
                        FilePartByteRange = fileRange,
                    }
                ],
            },
        };
}
