using MemoryPack;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using UsenetSharp.Models;
using UsenetSharp.Streams;
using static NzbWebDAV.Tests.Fakes.Rar4TestArchiveBuilder;

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
        var continuationBytes = BuildRar4ContinuationVolume("movie.mkv", 2_000);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);
        // Encoded trailing size must cover remaining uncompressed bytes, but the
        // 0.95*encoded estimate (minus header guess) must not overshoot remaining
        // or LazyRar falls back before the coverage bound matters.
        var trailing = FileInfoFor(
            "vol.r00",
            "r00@example.com",
            encodedBytes: 2_100,
            fileSize: null,
            first16KB: continuationBytes);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
            ["r00@example.com"] = continuationBytes,
        });

        var result = await new LazyRarProcessor([first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync() as LazyRarProcessor.Result;

        Assert.NotNull(result);
        Assert.Equal("movie.mkv", result!.PathInArchive);
        Assert.Equal(uncompressed, result!.TotalFileSize);
        Assert.Single(result!.PendingParts);
    }

    [Fact]
    public async Task ProcessAsync_OmittedSegmentWithoutPar2Size_FallsBackToEager()
    {
        var volumeBytes = BuildRar4SplitFirstVolume("movie.mkv", 1_000, 3_000);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);
        var trailing = FileInfoFor("vol.r00", "r00@example.com", 2_100, fileSize: null);
        trailing.NzbFile.Segments.Add(new NzbSegment
        {
            MessageId = NzbFile.CreateOmittedSegmentId("r00@example.com", 2),
            Number = 2,
            Bytes = 2_100,
        });
        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
        });

        Assert.Null(await new LazyRarProcessor([first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync());
    }

    [Fact]
    public async Task ProcessAsync_UnderestimatedFirstVolumeSize_ContainsPackedRange()
    {
        const int packed = 1_000;
        const int uncompressed = 3_000;
        var volumeBytes = BuildRar4SplitFirstVolume("movie.mkv", packed, uncompressed);
        var continuationBytes = BuildRar4ContinuationVolume("movie.mkv", 2_000);
        var underestimatedSize = volumeBytes.Length - 100;
        var first = FileInfoFor(
            "vol.rar",
            "first@example.com",
            volumeBytes.Length,
            underestimatedSize);
        var trailing = FileInfoFor(
            "vol.r00",
            "r00@example.com",
            encodedBytes: 2_100,
            fileSize: null,
            first16KB: continuationBytes);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
            ["r00@example.com"] = continuationBytes,
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
        var continuationBytes = BuildRar4ContinuationVolume(
            "b082fa0beaa644d3aa01045d5b8d0b36.xyz", 2_000);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);
        var trailing = FileInfoFor(
            "vol.r00",
            "r00@example.com",
            encodedBytes: 2_100,
            fileSize: null,
            first16KB: continuationBytes);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
            ["r00@example.com"] = continuationBytes,
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
        var continuationBytes = BuildRar4ContinuationVolume("movie.mkv", 2_000);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);
        var trailing = FileInfoFor(
            "vol.r00",
            "r00@example.com",
            encodedBytes: 2_100,
            fileSize: null,
            first16KB: continuationBytes);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
            ["r00@example.com"] = continuationBytes,
        });

        var result = await new LazyRarProcessor([first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync() as LazyRarProcessor.Result;

        Assert.NotNull(result);
        Assert.Null(result!.SniffedVideoExtension);
    }

    [Fact]
    public async Task ProcessAsync_MemberEndsBeforeLastVolume_ReturnsNull()
    {
        const string member = "movie.mkv";
        var firstBytes = BuildRar4SplitFirstVolume(member, packedSize: 1_000, uncompressedSize: 1_800);
        var finalMemberBytes = BuildRar4ContinuationVolume(member, packedSize: 800);
        var extraBytes = BuildRar4Volume(
            "extra.srt",
            packedSize: 100,
            uncompressedSize: 100,
            firstVolume: false,
            splitBefore: false,
            splitAfter: false);
        var infos = new List<GetFileInfosStep.FileInfo>
        {
            FileInfoFor("opaque.part01.rar", "part1@example.com", firstBytes.Length, firstBytes.Length),
            FileInfoFor(
                "opaque.part02.rar",
                "part2@example.com",
                finalMemberBytes.Length,
                finalMemberBytes.Length,
                finalMemberBytes),
            FileInfoFor(
                "opaque.part03.rar",
                "part3@example.com",
                extraBytes.Length,
                extraBytes.Length,
                extraBytes),
        };

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["part1@example.com"] = firstBytes,
        });

        var result = await new LazyRarProcessor(infos, client, password: null, CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ProcessAsync_MemberSpansEveryVolume_MountsLazily()
    {
        const string member = "movie.mkv";
        var firstBytes = BuildRar4SplitFirstVolume(member, packedSize: 600, uncompressedSize: 1_800);
        var middleBytes = BuildRar4ContinuationVolume(member, packedSize: 600, splitAfter: true);
        var finalBytes = BuildRar4ContinuationVolume(member, packedSize: 600);
        var infos = new List<GetFileInfosStep.FileInfo>
        {
            FileInfoFor("opaque.part01.rar", "part1@example.com", firstBytes.Length, firstBytes.Length),
            FileInfoFor(
                "opaque.part02.rar",
                "part2@example.com",
                middleBytes.Length,
                middleBytes.Length,
                middleBytes),
            FileInfoFor(
                "opaque.part03.rar",
                "part3@example.com",
                finalBytes.Length,
                finalBytes.Length,
                finalBytes),
        };

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["part1@example.com"] = firstBytes,
            ["part3@example.com"] = finalBytes,
        });

        var result = Assert.IsType<LazyRarProcessor.Result>(
            await new LazyRarProcessor(infos, client, password: null, CancellationToken.None)
                .ProcessAsync());

        Assert.Equal(member, result.PathInArchive);
        Assert.Equal(2, result.PendingParts.Length);
        Assert.Equal(true, result.FirstPart.IsSplitAfter);

        // Validation parsed every continuation header, so import keeps their ranges.
        var resolved = Assert.IsType<DavMultipartFile.FilePart[]>(result.ResolvedTrailingParts);
        Assert.Equal(2, resolved.Length);
        Assert.All(resolved, part => Assert.Equal(600, part.FilePartByteRange.Count));
        Assert.True(resolved[0].IsSplitAfter);
        Assert.False(resolved[1].IsSplitAfter);
        Assert.Equal(result.PendingParts[1].SegmentIds, resolved[1].SegmentIds);
        Assert.Equal(1_800, result.FirstPart.FilePartByteRange.Count + resolved.Sum(p => p.FilePartByteRange.Count));
    }

    [Fact]
    public async Task ResolvedContinuations_PersistNonLazyAndPlayWithoutResolver()
    {
        const string member = "movie.mkv";
        var volumes = new[]
        {
            BuildRar4SplitFirstVolume(member, packedSize: 600, uncompressedSize: 1_800),
            BuildRar4ContinuationVolume(member, packedSize: 600, splitAfter: true),
            BuildRar4ContinuationVolume(member, packedSize: 600),
        };
        var expected = new byte[1_800];
        for (var v = 0; v < volumes.Length; v++)
        {
            var payload = volumes[v].AsSpan(volumes[v].Length - 600);
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(v * 600 + i);
            payload.CopyTo(expected.AsSpan(v * 600));
        }

        var infos = new List<GetFileInfosStep.FileInfo>
        {
            FileInfoFor("opaque.part01.rar", "part1@example.com", volumes[0].Length, volumes[0].Length),
            FileInfoFor("opaque.part02.rar", "part2@example.com", volumes[1].Length, volumes[1].Length, volumes[1]),
            FileInfoFor("opaque.part03.rar", "part3@example.com", volumes[2].Length, volumes[2].Length, volumes[2]),
        };
        // A trusted index known at import (e.g. from a yEnc header) must survive; an absent one stays absent.
        infos[1].NzbFile.Segments[0].ByteRange = LongRange.FromStartAndSize(0, volumes[1].Length);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["part1@example.com"] = volumes[0],
            ["part2@example.com"] = volumes[1],
            ["part3@example.com"] = volumes[2],
        });
        var result = Assert.IsType<LazyRarProcessor.Result>(
            await new LazyRarProcessor(infos, client, password: null, CancellationToken.None).ProcessAsync());

        var meta = AggregateAndRoundTrip(result);

        Assert.False(meta.IsLazy);
        Assert.Empty(meta.PendingParts);
        Assert.Equal(3, meta.FileParts.Length);
        Assert.Equal(1_800, meta.ExpectedFileSize);
        Assert.True(meta.FileParts[1].SegmentByteRangesTrusted);
        Assert.Equal(
            [LongRange.FromStartAndSize(0, volumes[1].Length)],
            Assert.IsType<LongRange[]>(meta.FileParts[1].SegmentByteRanges));

        // No resolver: a lazy layout would fail here, so playback proves the persisted ranges.
        await using var stream = new DavMultipartFileStream(
            new DavMultipartFile { Id = Guid.NewGuid(), Metadata = meta },
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: member);
        Assert.Equal(1_800, stream.Length);
        var all = new byte[1_800];
        await stream.ReadExactlyAsync(all);
        Assert.Equal(expected, all);

        stream.Seek(1_500, SeekOrigin.Begin);
        var tail = new byte[300];
        await stream.ReadExactlyAsync(tail);
        Assert.Equal(expected[1_500..], tail);
    }

    [Fact]
    public async Task ContinuationSizesWithinTolerance_PersistLazyLayout()
    {
        const string member = "movie.mkv";
        var firstBytes = BuildRar4SplitFirstVolume(member, packedSize: 600, uncompressedSize: 1_800);
        var middleBytes = BuildRar4ContinuationVolume(member, packedSize: 600, splitAfter: true);
        // 8 bytes short: passes chain validation tolerance but cannot publish the exact size.
        var finalBytes = BuildRar4ContinuationVolume(member, packedSize: 592);
        var infos = new List<GetFileInfosStep.FileInfo>
        {
            FileInfoFor("opaque.part01.rar", "part1@example.com", firstBytes.Length, firstBytes.Length),
            FileInfoFor("opaque.part02.rar", "part2@example.com", middleBytes.Length, middleBytes.Length, middleBytes),
            FileInfoFor("opaque.part03.rar", "part3@example.com", finalBytes.Length, finalBytes.Length, finalBytes),
        };
        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["part1@example.com"] = firstBytes,
            ["part3@example.com"] = finalBytes,
        });

        var result = Assert.IsType<LazyRarProcessor.Result>(
            await new LazyRarProcessor(infos, client, password: null, CancellationToken.None).ProcessAsync());
        Assert.Null(result.ResolvedTrailingParts);

        var meta = AggregateAndRoundTrip(result);
        Assert.True(meta.IsLazy);
        Assert.Single(meta.FileParts);
        Assert.Equal(2, meta.PendingParts.Length);
    }

    private static DavMultipartFile.Meta AggregateAndRoundTrip(LazyRarProcessor.Result result)
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"lazy-rar-agg-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options);
            context.Database.EnsureCreated();
            var mount = DavItem.New(
                Guid.NewGuid(), DavItem.ContentFolder, "release", null,
                DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
                null, null, Guid.NewGuid(), null, nzbBlobId: null, arrDownloadId: null);
            context.Items.Add(mount);

            new RarAggregator(new DavDatabaseClient(context), mount, checkedFullHealth: false)
                .UpdateDatabase([result]);

            var blob = Assert.Single(context.BlobMultipartFiles);
            return MemoryPackSerializer.Deserialize<DavMultipartFile>(MemoryPackSerializer.Serialize(blob))!.Metadata;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch (IOException) { /* ignore */ }
        }
    }

    [Fact]
    public async Task ProcessAsync_ContinuationWithoutSplitBefore_ReturnsNull()
    {
        const string member = "movie.mkv";
        var firstBytes = BuildRar4SplitFirstVolume(member, packedSize: 600, uncompressedSize: 1_200);
        var invalidContinuation = BuildRar4Volume(
            member,
            packedSize: 600,
            uncompressedSize: 600,
            firstVolume: false,
            splitBefore: false,
            splitAfter: false);
        var first = FileInfoFor(
            "opaque.part01.rar", "part1@example.com", firstBytes.Length, firstBytes.Length);
        var trailing = FileInfoFor(
            "opaque.part02.rar",
            "part2@example.com",
            invalidContinuation.Length,
            invalidContinuation.Length,
            invalidContinuation);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["part1@example.com"] = firstBytes,
        });

        var result = await new LazyRarProcessor(
                [first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ProcessAsync_ContinuationWithDifferentMember_ReturnsNull()
    {
        const string member = "movie.mkv";
        var firstBytes = BuildRar4SplitFirstVolume(member, packedSize: 600, uncompressedSize: 1_200);
        var wrongMember = BuildRar4ContinuationVolume("different.mkv", packedSize: 600);
        var first = FileInfoFor(
            "opaque.part01.rar", "part1@example.com", firstBytes.Length, firstBytes.Length);
        var trailing = FileInfoFor(
            "opaque.part02.rar",
            "part2@example.com",
            wrongMember.Length,
            wrongMember.Length,
            wrongMember);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["part1@example.com"] = firstBytes,
        });

        var result = await new LazyRarProcessor(
                [first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ProcessAsync_ContinuationSizeMismatch_ReturnsNull()
    {
        const string member = "movie.mkv";
        var firstBytes = BuildRar4SplitFirstVolume(member, packedSize: 600, uncompressedSize: 1_300);
        var finalBytes = BuildRar4ContinuationVolume(member, packedSize: 600);
        var first = FileInfoFor(
            "opaque.part01.rar", "part1@example.com", firstBytes.Length, firstBytes.Length);
        var trailing = FileInfoFor(
            "opaque.part02.rar",
            "part2@example.com",
            finalBytes.Length,
            finalBytes.Length,
            finalBytes);

        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["part1@example.com"] = firstBytes,
        });

        var result = await new LazyRarProcessor(
                [first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ProcessAsync_EncryptedMemberWithoutPassword_ReturnsNull()
    {
        var volumeBytes = BuildRar4Volume(
            "movie.mkv",
            packedSize: 100,
            uncompressedSize: 100,
            firstVolume: true,
            splitBefore: false,
            splitAfter: false,
            encrypted: true);
        var first = FileInfoFor(
            "opaque.rar", "part1@example.com", volumeBytes.Length, volumeBytes.Length);
        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["part1@example.com"] = volumeBytes,
        });

        var result = await new LazyRarProcessor(
                [first], client, password: null, CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ProcessAsync_ContinuationEncryptionStateChanges_ReturnsNull()
    {
        const string member = "movie.mkv";
        var firstBytes = BuildRar4SplitFirstVolume(member, packedSize: 600, uncompressedSize: 1_200);
        var encryptedContinuation = BuildRar4Volume(
            member,
            packedSize: 600,
            uncompressedSize: 600,
            firstVolume: false,
            splitBefore: true,
            splitAfter: false,
            encrypted: true);
        var first = FileInfoFor(
            "opaque.part01.rar", "part1@example.com", firstBytes.Length, firstBytes.Length);
        var trailing = FileInfoFor(
            "opaque.part02.rar",
            "part2@example.com",
            encryptedContinuation.Length,
            encryptedContinuation.Length,
            encryptedContinuation);
        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["part1@example.com"] = firstBytes,
        });

        var result = await new LazyRarProcessor(
                [first, trailing], client, password: null, CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task ProcessAsync_SingleVolumeWithSecondMember_ReturnsNull()
    {
        var volumeBytes = BuildRar4Volume("Episode01.mkv", 800, firstVolume: true,
            trailingMembers: [("Episode02.mkv", 200)]);
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
    public async Task ProcessAsync_SingleVolumeWithSecondMember_WithoutPar2Size_ReturnsNull()
    {
        var volumeBytes = BuildRar4Volume("Episode01.mkv", 800, firstVolume: true,
            trailingMembers: [("Episode02.mkv", 200)]);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, fileSize: null);
        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
        });

        Assert.Null(await new LazyRarProcessor([first], client, password: null, CancellationToken.None)
            .ProcessAsync());
    }

    [Fact]
    public async Task ProcessAsync_MultipartFinalVolumeWithAnotherMember_ReturnsNull()
    {
        const string member = "movie.mkv";
        var firstBytes = BuildRar4SplitFirstVolume(member, packedSize: 600, uncompressedSize: 1_200);
        var finalBytes = BuildRar4ContinuationVolume(member, packedSize: 600,
            trailingMembers: [("sample.mkv", 100)]);
        var first = FileInfoFor("opaque.part01.rar", "part1@example.com", firstBytes.Length, firstBytes.Length);
        var last = FileInfoFor("opaque.part02.rar", "part2@example.com", finalBytes.Length, fileSize: null, finalBytes);
        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["part1@example.com"] = firstBytes,
            ["part2@example.com"] = finalBytes,
        });

        Assert.Null(await new LazyRarProcessor([first, last], client, password: null, CancellationToken.None)
            .ProcessAsync());
    }

    [Fact]
    public async Task ProcessAsync_TrueSingleMemberSingleVolume_StillMounts()
    {
        var volumeBytes = BuildRar4Volume("movie.mkv", 1_000, firstVolume: true);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);
        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
        });

        var result = Assert.IsType<LazyRarProcessor.Result>(
            await new LazyRarProcessor([first], client, password: null, CancellationToken.None).ProcessAsync());

        Assert.Equal("movie.mkv", result.PathInArchive);
        Assert.Empty(result.PendingParts);
    }

    [Fact]
    public async Task ProcessAsync_DirectoryHeaderAfterMember_DoesNotRejectLazy()
    {
        var volumeBytes = BuildRar4Volume("movie.mkv", 1_000, firstVolume: true,
            trailingDirectories: ["Extras"]);
        var first = FileInfoFor("vol.rar", "first@example.com", volumeBytes.Length, volumeBytes.Length);
        using var client = new MemoryServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = volumeBytes,
        });

        Assert.IsType<LazyRarProcessor.Result>(
            await new LazyRarProcessor([first], client, password: null, CancellationToken.None).ProcessAsync());
    }

    [Fact]
    public async Task ProcessAsync_TransientFailureDuringProof_IsRetryable()
    {
        const string member = "movie.mkv";
        var firstBytes = BuildRar4SplitFirstVolume(member, packedSize: 600, uncompressedSize: 1_200);
        var finalBytes = BuildRar4ContinuationVolume(member, packedSize: 600);
        var first = FileInfoFor("opaque.part01.rar", "part1@example.com", firstBytes.Length, firstBytes.Length);
        var last = FileInfoFor("opaque.part02.rar", "part2@example.com", finalBytes.Length, finalBytes.Length, finalBytes);
        using var client = new MemoryServingNntpClient(
            new Dictionary<string, byte[]> { ["part1@example.com"] = firstBytes, ["part2@example.com"] = finalBytes },
            bodyFailure: id => id == "part2@example.com" ? new IOException("provider blip") : null);

        var ex = await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            new LazyRarProcessor([first, last], client, password: null, CancellationToken.None).ProcessAsync());

        Assert.True(ex.TryGetCausingException<IOException>(out _));
    }

    [Fact]
    public async Task ProcessAsync_CancellationDuringProof_Propagates()
    {
        const string member = "movie.mkv";
        var firstBytes = BuildRar4SplitFirstVolume(member, packedSize: 600, uncompressedSize: 1_200);
        var finalBytes = BuildRar4ContinuationVolume(member, packedSize: 600);
        var first = FileInfoFor("opaque.part01.rar", "part1@example.com", firstBytes.Length, firstBytes.Length);
        var last = FileInfoFor("opaque.part02.rar", "part2@example.com", finalBytes.Length, finalBytes.Length, finalBytes);
        using var cts = new CancellationTokenSource();
        using var client = new MemoryServingNntpClient(
            new Dictionary<string, byte[]> { ["part1@example.com"] = firstBytes, ["part2@example.com"] = finalBytes },
            bodyFailure: id =>
            {
                if (id != "part2@example.com") return null;
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LazyRarProcessor([first, last], client, password: null, cts.Token).ProcessAsync());
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

    [Fact]
    public async Task BuildRar4Volume_TrailingMembers_AreEnumeratedAsSeparateFileHeaders()
    {
        var bytes = BuildRar4Volume("a.mkv", 300, firstVolume: true,
            trailingMembers: [("b.mkv", 100)]);
        await using var stream = new MemoryStream(bytes, writable: false);

        var headers = await RarUtil.GetRarHeadersAsync(stream, password: null, CancellationToken.None);

        Assert.Equal(["a.mkv", "b.mkv"],
            headers.OfType<SharpCompress.Common.Rar.Headers.IRarFileHeader>().Select(h => h.FileName));
    }

    private static GetFileInfosStep.FileInfo FileInfoFor(
        string fileName,
        string messageId,
        long encodedBytes,
        long? fileSize,
        byte[]? first16KB = null)
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
            First16KB = first16KB,
        };
    }

    /// <summary>
    /// Serves raw decoded bytes via CachedYencStream so tests do not depend on
    /// rapidyenc native (same approach as LazyRarResolverTests).
    /// </summary>
    private sealed class MemoryServingNntpClient(
        IReadOnlyDictionary<string, byte[]> segments,
        Func<string, Exception?>? bodyFailure = null) : NntpClient
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
            if (bodyFailure?.Invoke(key) is { } failure) throw failure;
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
