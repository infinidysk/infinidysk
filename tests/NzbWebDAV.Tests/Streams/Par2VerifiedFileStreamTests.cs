using System.IO.Hashing;
using System.Security.Cryptography;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Tests.Fakes;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Streams;

public class Par2VerifiedFileStreamTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NzbFileStream_PrefixProofAvoidsSliceReadButStillRejectsCorruption(bool corruptPrefix)
    {
        var data = Enumerable.Range(0, 32768).Select(value => (byte)(value % 251)).ToArray();
        var proof = CreateProof(data, 32768);
#pragma warning disable CA5351
        proof.File16kHash = MD5.HashData(data.AsSpan(0, 16384));
#pragma warning restore CA5351
        var prefix = data[..16384];
        if (corruptPrefix) prefix[0] ^= 1;
        var tail = data[16384..];
        tail[0] ^= 1;
        using var client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["prefix"] = prefix,
            ["tail"] = tail,
        }, useCachedYencStreams: true,
            segmentRanges: new Dictionary<string, NzbWebDAV.Models.LongRange>
            {
                ["prefix"] = new(0, 16384),
                ["tail"] = new(16384, 32768),
            });
        await using var stream = new NzbFileStream(["prefix", "tail"], 32768, client, 0,
            verificationProof: proof);
        var output = new byte[] { 255, 255, 255, 255 };
        if (corruptPrefix)
        {
            await Assert.ThrowsAsync<NzbWebDAV.Exceptions.NonRetryableDownloadException>(
                async () => await stream.ReadAsync(output));
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, output);
            Assert.Equal(0, stream.Position);
        }
        else
        {
            Assert.Equal(4, await stream.ReadAsync(output));
            Assert.Equal(data[..4], output);
        }
        Assert.DoesNotContain("tail", client.RequestedSegmentIds);

        stream.Position = 16384;
        Array.Fill(output, (byte)255);
        await Assert.ThrowsAsync<NzbWebDAV.Exceptions.NonRetryableDownloadException>(
            async () => await stream.ReadAsync(output));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, output);
        Assert.Equal(16384, stream.Position);
    }

    [Fact]
    public async Task NzbFileStream_UndersizedDeclaredFileLengthBypassesCacheStorageButAllowsVerifiedRemoteRead()
    {
        var data = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var directory = Directory.CreateTempSubdirectory("par2-proof-cache-");
        try
        {
            using var fake = new FakeNntpClient(new Dictionary<string, byte[]> { ["body"] = data },
                useCachedYencStreams: true, yencHeaders: new Dictionary<string, UsenetYencHeader>
                {
                    ["body"] = new()
                    {
                        FileName = "test.bin", LineLength = 128, FileSize = 1,
                        PartOffset = 0, PartSize = 64, PartNumber = 557, TotalParts = 931
                    }
                });
            using var providers = new MultiProviderNntpClient(
                [NzbWebDAV.Tests.Clients.Usenet.MultiProviderNntpClientTests.CreateProvider(fake)]);
            using var client = new SegmentCacheNntpClient(providers, directory.FullName, 1024 * 1024);
            await client.CatalogLoadTask;
            await using var stream = new NzbFileStream(["body"], 1, client, 4, verificationProof: CreateProof(data, 32));
            Assert.Equal(61, stream.Seek(-3, SeekOrigin.End));
            var output = new byte[3];
            Assert.Equal(3, await stream.ReadAsync(output));
            Assert.Equal(data[61..], output);
            Assert.Equal(0, client.CurrentBytes);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task NzbFileStream_ProofReadRetainsMissingArticleAlternateIdFallback()
    {
        var data = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        using var fake = new FakeNntpClient(new Dictionary<string, byte[]> { ["alternate"] = data }, useCachedYencStreams: true);
        using var client = new MultiProviderNntpClient(
            [NzbWebDAV.Tests.Clients.Usenet.MultiProviderNntpClientTests.CreateProvider(fake)]);
        await using var stream = new NzbFileStream(["missing"], 32, client, 4,
            segmentFallbacks: [["alternate"]], verificationProof: CreateProof(data, 32));
        var output = new byte[32];
        Assert.Equal(32, await stream.ReadAsync(output));
        Assert.Equal(data, output);
        Assert.Contains("alternate", fake.RequestedSegmentIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NzbFileStream_CachedCandidateIsVerifiedAndInvalidCandidateRetriesRemote(bool corruptCache)
    {
        var data = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var cached = (byte[])data.Clone();
        if (corruptCache) cached[63] ^= 1;
        var directory = Directory.CreateTempSubdirectory("par2-proof-cache-");
        try
        {
            using var fake = new FakeNntpClient(new Dictionary<string, byte[]> { ["body"] = cached }, useCachedYencStreams: true);
            using var providers = new MultiProviderNntpClient(
                [NzbWebDAV.Tests.Clients.Usenet.MultiProviderNntpClientTests.CreateProvider(fake)]);
            using var client = new SegmentCacheNntpClient(providers, directory.FullName, 1024 * 1024);
            await client.CatalogLoadTask;
            var seed = await client.DecodedBodyAsync("body", CancellationToken.None);
            await using (var seedStream = seed.Stream!)
                await seedStream.CopyToAsync(Stream.Null);
            Assert.Equal(1, fake.BodyRequestCount);
            Assert.True(client.CurrentBytes > 0);
            fake.Serve("body", data);
            await using var stream = new NzbFileStream(["body"], 64, client, 4, verificationProof: CreateProof(data, 64));
            var output = new byte[3];
            Assert.Equal(3, await stream.ReadAsync(output));
            Assert.Equal(data[..3], output);
            Assert.Equal(corruptCache ? 2 : 1, fake.BodyRequestCount);
            Assert.Null(MultiProviderNntpClient.AttributionContext.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task NzbFileStream_LaterCorruptSliceDoesNotReleasePrefixOrAdvancePosition()
    {
        var data = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var corrupt = (byte[])data.Clone();
        corrupt[63] ^= 1;
        using var fake = new FakeNntpClient(new Dictionary<string, byte[]> { ["body"] = corrupt }, useCachedYencStreams: true);
        using var client = new MultiProviderNntpClient(
            [NzbWebDAV.Tests.Clients.Usenet.MultiProviderNntpClientTests.CreateProvider(fake)]);
        await using var stream = new NzbFileStream(["body"], 64, client, 4, verificationProof: CreateProof(data, 32));
        var first = new byte[32];
        Assert.Equal(32, await stream.ReadAsync(first));
        Assert.Equal(data[..32], first);
        var output = new byte[] { 200, 201, 202 };
        var failure = await Assert.ThrowsAsync<NzbWebDAV.Exceptions.NonRetryableDownloadException>(async () => await stream.ReadAsync(output));
        Assert.Contains("bounded source attempts", failure.Message);
        Assert.True(NzbWebDAV.Extensions.ExceptionExtensions.TryGetKnownErrorMessage(failure, out _));
        Assert.Equal(new byte[] { 200, 201, 202 }, output);
        Assert.Equal(32, stream.Position);
        Assert.Null(MultiProviderNntpClient.AttributionContext.Value);
        Assert.Equal(fake.BodyRequestCount, fake.CompletionCallbackCount);
    }

    [Fact]
    public async Task NzbFileStream_InvalidProofFailsClosedAndDisposalOwnsVerifier()
    {
        using var fake = new FakeNntpClient(new Dictionary<string, byte[]> { ["body"] = new byte[32] }, useCachedYencStreams: true);
        var invalid = CreateProof(new byte[32], 32);
        invalid.SliceMd5 = [];
        await using (var stream = new NzbFileStream(["body"], 32, fake, 4, verificationProof: invalid))
        {
            var output = new byte[] { 200 };
            await Assert.ThrowsAsync<InvalidDataException>(async () => await stream.ReadAsync(output));
            Assert.Equal(200, output[0]);
            Assert.Equal(0, fake.BodyRequestCount);
        }
        var disposed = new NzbFileStream(["body"], 32, fake, 4, verificationProof: CreateProof(new byte[32], 32));
        Assert.Equal(1, await disposed.ReadAsync(new byte[1]));
        await disposed.DisposeAsync();
        await disposed.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await disposed.ReadAsync(new byte[1]));
        Assert.Throws<ObjectDisposedException>(() => disposed.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public async Task NzbFileStream_ChecksumFailureRetriesWholeSliceOnBackup()
    {
        var data = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var corrupt = (byte[])data.Clone();
        corrupt[63] ^= 1;
        using var primary = new FakeNntpClient(new Dictionary<string, byte[]> { ["body"] = corrupt }, useCachedYencStreams: true);
        using var backup = new FakeNntpClient(new Dictionary<string, byte[]> { ["body"] = data }, useCachedYencStreams: true);
        using var client = new MultiProviderNntpClient(
            [
                NzbWebDAV.Tests.Clients.Usenet.MultiProviderNntpClientTests.CreateProvider(primary, host: "primary.example"),
                NzbWebDAV.Tests.Clients.Usenet.MultiProviderNntpClientTests.CreateProvider(backup, host: "backup.example", providerType: ProviderType.BackupOnly)
            ], articleMissCache: new ArticleMissNegativeCache(new NzbWebDAV.Config.ConfigManager()));
        await using var stream = new NzbFileStream(["body"], 64, client, 4, verificationProof: CreateProof(data, 64));
        var output = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(output));
        Assert.Equal(data[..3], output);
        Assert.Equal(2, primary.BodyRequestCount);
        Assert.Equal(1, backup.BodyRequestCount);
        Assert.Equal(3, stream.Position);
        Assert.Null(NzbWebDAV.Clients.Usenet.Contexts.Par2VerificationReadContext.PreferredProvider);
        Assert.Null(MultiProviderNntpClient.AttributionContext.Value);
        primary.Serve("body", data);
        await using var nextRead = new NzbFileStream(["body"], 64, client, 4, verificationProof: CreateProof(data, 64));
        Assert.Equal(3, await nextRead.ReadAsync(output));
        Assert.Equal(data[..3], output);
        Assert.Equal(3, primary.BodyRequestCount);
        Assert.Equal(1, backup.BodyRequestCount);
    }

    [Fact]
    public async Task NzbFileStream_ProofOwnsLengthSequentialReadsAndSeeks()
    {
        var data = Enumerable.Range(0, 35).Select(value => (byte)value).ToArray();
        var segments = new Dictionary<string, byte[]> { ["first"] = data[..16], ["last"] = data[16..] };
        var headers = new Dictionary<string, UsenetYencHeader>
        {
            ["first"] = new() { FileName = "test.bin", LineLength = 128, FileSize = 3, PartOffset = 0, PartSize = 16, TotalParts = 931, PartNumber = 557 },
            ["last"] = new() { FileName = "test.bin", LineLength = 128, FileSize = 3, PartOffset = 16, PartSize = 19, TotalParts = 800, PartNumber = 799 }
        };
        using var fake = new FakeNntpClient(segments, useCachedYencStreams: true, yencHeaders: headers);
        using var client = new MultiProviderNntpClient(
            [NzbWebDAV.Tests.Clients.Usenet.MultiProviderNntpClientTests.CreateProvider(fake)]);
        await using var stream = new NzbFileStream(["first", "last"], 3, client, 4,
            verificationProof: CreateProof(data, 32));
        Assert.Equal(35, stream.Length);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        Assert.Equal(data, output.ToArray());
        Assert.Equal(35, stream.Position);
        Assert.Equal(7, stream.Seek(7, SeekOrigin.Begin));
        var prefix = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(prefix));
        Assert.Equal(data[7..10], prefix);
        Assert.Equal(10, stream.Position);
        Assert.Equal(8, stream.Seek(-2, SeekOrigin.Current));
        Assert.Equal(32, stream.Seek(-3, SeekOrigin.End));
        Assert.Equal(3, await stream.ReadAsync(prefix));
        Assert.Equal(data[32..], prefix);
        Assert.Equal(0, await stream.ReadAsync(prefix));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NzbFileStream_SequentialSlicesShareOneReadAheadStreamAndRejectCorruption(bool corruptThirdSlice)
    {
        var data = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        var segments = Enumerable.Range(0, 8).ToDictionary(index => $"s{index}", index => data[(index * 16)..((index + 1) * 16)]);
        if (corruptThirdSlice)
            segments["s5"] = segments["s5"].Select((value, index) => index == 15 ? (byte)(value ^ 1) : value).ToArray();
        var headers = segments.Keys.Select((id, index) => (id, index)).ToDictionary(entry => entry.id, entry => new UsenetYencHeader
        {
            FileName = "test.bin", LineLength = 128, FileSize = 128, PartOffset = entry.index * 16, PartSize = 16,
            TotalParts = 8, PartNumber = entry.index + 1
        });
        using var fake = new FakeNntpClient(segments, useCachedYencStreams: true, yencHeaders: headers);
        using var client = new MultiProviderNntpClient(
            [NzbWebDAV.Tests.Clients.Usenet.MultiProviderNntpClientTests.CreateProvider(fake)]);
        await using var stream = new NzbFileStream(segments.Keys.ToArray(), 128, client, 4,
            verificationProof: CreateProof(data, 32));

        var first = new byte[8];
        Assert.Equal(8, await stream.ReadAsync(first));
        Assert.Equal(data[..8], first);
        // Read-ahead fetches later slices before the caller asks for them.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!fake.RequestedSegmentIds.Contains("s4") && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Contains("s4", fake.RequestedSegmentIds);

        var output = new byte[56];
        await stream.ReadExactlyAsync(output);
        Assert.Equal(data[8..64], output);
        if (corruptThirdSlice)
        {
            var untouched = new byte[] { 200, 201, 202 };
            await Assert.ThrowsAsync<NzbWebDAV.Exceptions.NonRetryableDownloadException>(async () => await stream.ReadAsync(untouched));
            Assert.Equal(new byte[] { 200, 201, 202 }, untouched);
            Assert.Equal(64, stream.Position);
        }
        else
        {
            using var rest = new MemoryStream();
            await stream.CopyToAsync(rest);
            Assert.Equal(data[64..], rest.ToArray());
            // One persistent candidate stream: no segment is re-fetched per slice.
            Assert.All(segments.Keys, id => Assert.Equal(1, fake.BodyRequestCounts[id]));
        }
        await stream.DisposeAsync();
        Assert.Equal(fake.BodyRequestCount, fake.CompletionCallbackCount);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(32L, false)]
    public async Task Read_SignalsAllSegmentsIssuedOnlyWhenReadingToFileEnd(long? readBudget, bool expected)
    {
        var data = Enumerable.Range(0, 96).Select(value => (byte)value).ToArray();
        await using var stream = CreateSequential(data, 32, bufferSlices: 4,
            (start, _) => new CandidateStream(data) { Position = start }, readBudget);
        var output = new byte[32];
        Assert.Equal(32, await stream.ReadAsync(output));
        var progress = (ISegmentIssueProgress)stream;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (progress.AllSegmentsIssued != expected && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        // A producer that stops before the file end must not invite the next part ahead of the reader.
        Assert.Equal(expected, progress.AllSegmentsIssued);
    }

    [Fact]
    public async Task Read_SignalsAllSegmentsIssuedOnlyAfterEveryRemainingSliceVerifies()
    {
        var data = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        var corrupt = data.ToArray();
        corrupt[40] ^= 1;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = 0;
        await using var stream = CreateSequential(data, 32, bufferSlices: 4,
            (start, _) => new CandidateStream(corrupt, gateAt: start == 0 ? 32 : null, gate.Task) { Position = start },
            recover: (start, target, _, _) =>
            {
                Interlocked.Increment(ref recovered);
                data.AsMemory((int)start, target.Length).CopyTo(target);
                return Task.CompletedTask;
            });
        var progress = (ISegmentIssueProgress)stream;
        var output = new byte[32];
        Assert.Equal(32, await stream.ReadAsync(output));
        // The candidate has issued everything, but the middle slice is not verified yet.
        await Task.Delay(50);
        Assert.False(progress.AllSegmentsIssued);

        gate.SetResult();
        Assert.Equal(32, await stream.ReadAsync(output));
        Assert.Equal(data[32..64], output);
        Assert.Equal(1, recovered);
        // Reading resumes on a new candidate after the recovered slice.
        Assert.Equal(32, await stream.ReadAsync(output));
        Assert.Equal(data[64..96], output);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!progress.AllSegmentsIssued && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(progress.AllSegmentsIssued);
        using var rest = new MemoryStream();
        await stream.CopyToAsync(rest);
        Assert.Equal(data[96..], rest.ToArray());
    }

    [Theory]
    [InlineData(1, 32)]
    [InlineData(3, 96)]
    public async Task Read_PausedReaderHoldsAtMostBufferSlices(int bufferSlices, int expectedCandidateBytes)
    {
        var data = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        CandidateStream? candidate = null;
        await using var stream = CreateSequential(data, 32, bufferSlices,
            (start, _) => candidate = new CandidateStream(data) { Position = start });
        var output = new byte[32];
        Assert.Equal(32, await stream.ReadAsync(output));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((candidate?.Position ?? 0) < expectedCandidateBytes && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Task.Delay(100);
        // The reader's slice counts against the cap, so a paused reader stops the producer.
        Assert.Equal(expectedCandidateBytes, candidate!.Position);
        Assert.Equal(32, await stream.ReadAsync(output));
        Assert.Equal(data[32..64], output);
    }

    [Theory]
    [InlineData(Par2FileProof.MaxVerificationSliceSize, 0L, 1)]
    [InlineData(Par2FileProof.MaxVerificationSliceSize, long.MaxValue, 1)]
    [InlineData(8 * 1024 * 1024, 0L, 2)]
    [InlineData(1_536_000, 0L, 4)]
    [InlineData(1_536_000, long.MaxValue, 10)]
    public void GetBufferSlices_BoundsVerificationMemoryByBytes(int sliceSize, long readAheadBytes, int expected)
    {
        var slices = Par2VerifiedFileStream.GetBufferSlices(readAheadBytes, sliceSize);
        Assert.Equal(expected, slices);
        Assert.True(slices == 1 || (long)slices * sliceSize <= Par2VerifiedFileStream.MaximumBufferBytes);
    }

    private static Par2VerifiedFileStream CreateSequential(
        byte[] data, int sliceSize, int bufferSlices, Func<long, long, Stream> open, long? readBudget = null,
        Func<long, Memory<byte>, Exception?, CancellationToken, Task>? recover = null) =>
        new(CreateProof(data, sliceSize), (_, _, _) => throw new InvalidOperationException("Unexpected slice read."),
            sequential: new Par2SequentialCandidateSource(open, () => new MemoryStream(),
                recover ?? ((_, _, _, _) => throw new InvalidOperationException("Unexpected recovery.")),
                () => readBudget, bufferSlices));

    // Reports all segments issued as soon as it opens; reads at or past gateAt wait for the gate.
    private sealed class CandidateStream(byte[] data, long? gateAt = null, Task? gate = null)
        : MemoryStream(data, writable: false), ISegmentIssueProgress
    {
        bool ISegmentIssueProgress.AllSegmentsIssued => true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= gateAt) await gate!.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public void Seek_PreservesNzbFileStreamBounds()
    {
        using var stream = new Par2VerifiedFileStream(CreateProof(new byte[32], 32),
            (_, _, _) => Task.CompletedTask);
        Assert.Equal(32, stream.Seek(0, SeekOrigin.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(1, SeekOrigin.Current));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(long.MaxValue, SeekOrigin.Current));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Equal(32, stream.Position);
    }

    [Fact]
    public async Task Read_VerifiesWholeSliceBeforeReturningPrefix()
    {
        var data = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var proof = CreateProof(data, 32);
        var reads = new List<(long Start, int Length)>();
        await using var stream = new Par2VerifiedFileStream(proof, (start, target, _) =>
        {
            reads.Add((start, target.Length));
            data.AsMemory((int)start, target.Length).CopyTo(target);
            return Task.CompletedTask;
        });
        stream.Position = 7;
        var output = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(output));
        Assert.Equal(data[7..10], output);
        Assert.Equal((0L, 32), Assert.Single(reads));
        Assert.Equal(3, await stream.ReadAsync(output));
        Assert.Single(reads);
    }

    [Fact]
    public async Task Read_RejectsLaterCorruptionWithoutTouchingCallerBuffer()
    {
        var data = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var proof = CreateProof(data, 32);
        data[63] ^= 1;
        await using var stream = new Par2VerifiedFileStream(proof, (start, target, _) =>
        {
            data.AsMemory((int)start, target.Length).CopyTo(target);
            return Task.CompletedTask;
        });
        Assert.Equal(32, await stream.ReadAsync(new byte[32]));
        var output = new byte[] { 200 };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await stream.ReadAsync(output));
        Assert.Equal(200, output[0]);
        Assert.Equal(32, stream.Position);
    }

    [Fact]
    public async Task Read_ZeroPadsFinalSliceAndHonorsCancellation()
    {
        var data = Enumerable.Range(0, 35).Select(value => (byte)value).ToArray();
        await using var stream = new Par2VerifiedFileStream(CreateProof(data, 32), (start, target, _) =>
        {
            data.AsMemory((int)start, target.Length).CopyTo(target);
            return Task.CompletedTask;
        });
        stream.Seek(-3, SeekOrigin.End);
        var output = new byte[5];
        Assert.Equal(3, await stream.ReadAsync(output));
        Assert.Equal(data[32..], output[..3]);
        Assert.Equal(0, await stream.ReadAsync(output));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadAsync(output, cancelled.Token));
    }

#pragma warning disable CA5351
    internal static Par2FileProof CreateProof(byte[] data, int sliceSize)
    {
        var count = (data.Length - 1) / sliceSize + 1;
        var proof = new Par2FileProof
        {
            FileLength = data.Length,
            SliceSize = sliceSize,
            FileId = new byte[16],
            FileHash = MD5.HashData(data),
            SliceMd5 = new byte[count * 16],
            SliceCrc32 = new uint[count]
        };
        for (var index = 0; index < count; index++)
        {
            var padded = new byte[sliceSize];
            data.AsSpan(index * sliceSize, Math.Min(sliceSize, data.Length - index * sliceSize)).CopyTo(padded);
            MD5.HashData(padded).CopyTo(proof.SliceMd5, index * 16);
            proof.SliceCrc32[index] = Crc32.HashToUInt32(padded);
        }
        return proof;
    }
#pragma warning restore CA5351
}