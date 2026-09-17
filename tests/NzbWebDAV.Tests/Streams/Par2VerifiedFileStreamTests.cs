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
    private static Par2FileProof CreateProof(byte[] data, int sliceSize)
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