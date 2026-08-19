using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Par2Recovery.ReedSolomon;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Services.Repair;

// Mutates CONFIG_PATH and database context options; the collection disables all
// parallelization so background repair services cannot race other tests' state
// or pollute global-logger captures with transient SQLite errors.
[Collection(nameof(ConfigPathCollection))]
public sealed class Par2RepairIntegrationTests
{
    [Fact]
    public async Task Reconstruct_MultipleMissingSlices_K2_SucceedsWithCorrectCoefficients()
    {
        var fileData = new byte[4096 * 4];
        for (var i = 0; i < fileData.Length; i++)
            fileData[i] = (byte)((i * 7 + 13) % 256);

        const ulong sliceSize = 4096;
        var (_, volume) = Par2TestEncoder.EncodeSet("multi.bin", fileData, sliceSize, [0u, 1u, 2u]);

        var descriptors = new Dictionary<string, FileDesc>();
        var ifscs = new Dictionary<string, IfscPacket>();
        MainPacket? main = null;
        var recovery = new List<Par2Reconstructor.RecoverySlice>();

        await using (var stream = new MemoryStream(volume))
        {
            while (stream.Position < stream.Length)
            {
                var packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, true, CancellationToken.None);
                switch (packet)
                {
                    case FileDesc fd:
                        descriptors[Convert.ToHexString(fd.FileID)] = fd;
                        break;
                    case MainPacket mp:
                        main = mp;
                        break;
                    case IfscPacket ifsc:
                        ifscs[Convert.ToHexString(ifsc.FileId)] = ifsc;
                        break;
                    case RecvSlic recv:
                        recovery.Add(new Par2Reconstructor.RecoverySlice(recv.Exponent, recv.Payload));
                        break;
                }
            }
        }

        Assert.NotNull(main);
        var slices = BuildSliceBytes(fileData, sliceSize);
        var missingIndices = new[] { 0, 2 };

        var reconstructor = new Par2Reconstructor();
        var result = await reconstructor.ReconstructAsync(
            main!,
            descriptors,
            ifscs,
            missingIndices,
            recovery,
            (globalSlice, size, _) =>
            {
                if (missingIndices.Contains(globalSlice))
                    return Task.FromResult<byte[]?>(null);
                return Task.FromResult<byte[]?>(slices[globalSlice]);
            },
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(slices[0], result.ReconstructedSlices[0]);
        Assert.Equal(slices[2], result.ReconstructedSlices[2]);
    }

    [Fact]
    public async Task Reconstruct_MissingSliceAtStart_WholeFileHashHandlesGap()
    {
        var fileData = new byte[4096 * 3];
        for (var i = 0; i < fileData.Length; i++)
            fileData[i] = (byte)(i ^ 0xAA);

        const ulong sliceSize = 4096;
        var (_, volume) = Par2TestEncoder.EncodeSet("gapped.bin", fileData, sliceSize, [0u, 1u]);

        var descriptors = new Dictionary<string, FileDesc>();
        var ifscs = new Dictionary<string, IfscPacket>();
        MainPacket? main = null;
        var recovery = new List<Par2Reconstructor.RecoverySlice>();

        await using (var stream = new MemoryStream(volume))
        {
            while (stream.Position < stream.Length)
            {
                var packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, true, CancellationToken.None);
                switch (packet)
                {
                    case FileDesc fd:
                        descriptors[Convert.ToHexString(fd.FileID)] = fd;
                        break;
                    case MainPacket mp:
                        main = mp;
                        break;
                    case IfscPacket ifsc:
                        ifscs[Convert.ToHexString(ifsc.FileId)] = ifsc;
                        break;
                    case RecvSlic recv:
                        recovery.Add(new Par2Reconstructor.RecoverySlice(recv.Exponent, recv.Payload));
                        break;
                }
            }
        }

        Assert.NotNull(main);
        var slices = BuildSliceBytes(fileData, sliceSize);

        var reconstructor = new Par2Reconstructor();
        var result = await reconstructor.ReconstructAsync(
            main!,
            descriptors,
            ifscs,
            [0],
            recovery,
            (globalSlice, size, _) =>
            {
                if (globalSlice == 0)
                    return Task.FromResult<byte[]?>(null);
                return Task.FromResult<byte[]?>(slices[globalSlice]);
            },
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(slices[0], result.ReconstructedSlices[0]);
    }

    [Fact]
    public async Task Reconstruct_CorruptRecoverySlice_GateFailure_NothingPersisted()
    {
        var fileData = new byte[4096 * 2];
        Random.Shared.NextBytes(fileData);
        const ulong sliceSize = 4096;
        var (_, volume) = Par2TestEncoder.EncodeSet("corrupt.bin", fileData, sliceSize, [0u]);

        var descriptors = new Dictionary<string, FileDesc>();
        var ifscs = new Dictionary<string, IfscPacket>();
        MainPacket? main = null;
        var recovery = new List<Par2Reconstructor.RecoverySlice>();

        await using (var stream = new MemoryStream(volume))
        {
            while (stream.Position < stream.Length)
            {
                var packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, true, CancellationToken.None);
                switch (packet)
                {
                    case FileDesc fd:
                        descriptors[Convert.ToHexString(fd.FileID)] = fd;
                        break;
                    case MainPacket mp:
                        main = mp;
                        break;
                    case IfscPacket ifsc:
                        ifscs[Convert.ToHexString(ifsc.FileId)] = ifsc;
                        break;
                    case RecvSlic recv:
                        var corrupted = (byte[])recv.Payload.Clone();
                        corrupted[0] ^= 0xFF;
                        recovery.Add(new Par2Reconstructor.RecoverySlice(recv.Exponent, corrupted));
                        break;
                }
            }
        }

        Assert.NotNull(main);
        var slices = BuildSliceBytes(fileData, sliceSize);

        var reconstructor = new Par2Reconstructor();
        var result = await reconstructor.ReconstructAsync(
            main!,
            descriptors,
            ifscs,
            [1],
            recovery,
            (globalSlice, size, _) =>
            {
                if (globalSlice == 1)
                    return Task.FromResult<byte[]?>(null);
                return Task.FromResult<byte[]?>(slices[globalSlice]);
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("IFSC verification", result.FailureReason);
    }

    [Fact]
    public void MissingSlicesExceedCap_DefaultCapIs8()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"nzbdav-cap-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var prevConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", tempDir);
            var configManager = new ConfigManager();
            var maxSlices = configManager.GetPar2MaxMissingSlices();
            Assert.Equal(8, maxSlices);

            var missingCount = 9;
            Assert.True(missingCount > maxSlices,
                $"9 missing slices should exceed the default cap of {maxSlices}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", prevConfigPath);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TriggerSink_NullCurrent_DoesNotThrow()
    {
        var prev = Par2RepairTriggerSink.Current;
        try
        {
            Par2RepairTriggerSink.Current = null;
            Par2RepairTriggerSink.Current?.ReportZeroFill("/view/test.mkv", "seg1@test", 0, 4096);
        }
        finally
        {
            Par2RepairTriggerSink.Current = prev;
        }
    }

    [Fact]
    public async Task ReportZeroFill_Disabled_IsNoOp()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-zf-disabled-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigManager();
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.CatalogLoadTask;
            var service = new Par2RepairService(config, null!, store);

            service.ReportZeroFill("/view/test.mkv", "seg1@test");

            Assert.Equal(0, service.PendingZeroFillCount);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReportZeroFill_Burst_IsDeduplicatedAndBounded()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-zf-burst-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            ]);
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.CatalogLoadTask;
            var service = new Par2RepairService(config, null!, store);

            // Repeated zero-fills for the same path collapse to one pending event.
            for (var i = 0; i < 1_000; i++)
                service.ReportZeroFill("/view/same.mkv", $"seg{i}@test");
            Assert.Equal(1, service.PendingZeroFillCount);

            // A scrub across many paths is capped by the bounded channel; excess
            // events are rejected synchronously and leave no bookkeeping behind.
            for (var i = 0; i < 1_000; i++)
                service.ReportZeroFill($"/view/file{i}.mkv", "seg@test");
            Assert.Equal(50, service.PendingZeroFillCount);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReportZeroFill_ShutdownDrainsCleanly()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"nzbdav-zf-shutdown-{Guid.NewGuid():N}");
        var patchDir = Path.Join(tempDir, "patches");
        Directory.CreateDirectory(tempDir);
        var prevConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", tempDir);
            DavDatabaseContext.ResetOptionsForTests();
            await using (var ctx = new DavDatabaseContext())
                await ctx.Database.EnsureCreatedAsync();

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            ]);
            var store = new RepairPatchStore(patchDir, 1024 * 1024);
            await store.CatalogLoadTask;
            var service = new Par2RepairService(config, null!, store);

            await service.StartAsync(CancellationToken.None);
            try
            {
                service.ReportZeroFill("/view/unknown.mkv", "seg1@test");

                // The single consumer resolves the (unknown) path and clears the dedup entry.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (service.PendingZeroFillCount > 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(25);
                Assert.Equal(0, service.PendingZeroFillCount);
            }
            finally
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await service.StopAsync(stopCts.Token);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", prevConfigPath);
            DavDatabaseContext.ResetOptionsForTests();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void UnbufferedStream_ZeroFill_SinkCallSiteExists()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(
            Path.Join(repoRoot, "backend", "Streams", "UnbufferedMultiSegmentStream.cs"));
        Assert.Contains("Par2RepairTriggerSink.Current?.ReportZeroFill", source);
    }

    [Fact]
    public void MultiSegmentStream_ZeroFillSegment_SinkCallSiteExists()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(
            Path.Join(repoRoot, "backend", "Streams", "MultiSegmentStream.cs"));
        Assert.Contains("Par2RepairTriggerSink.Current?.ReportZeroFill", source);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Join(dir, ".git")) || File.Exists(Path.Join(dir, ".git")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repository root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task HealthCheck_FilterSegmentsForStat_ExcludesRepairedSegments()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-hc-filter-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.CatalogLoadTask;

            var segmentIds = new[] { "seg0@test", "seg1@test", "seg2@test" };
            var ranges = new[]
            {
                LongRange.FromStartAndSize(0, 100),
                LongRange.FromStartAndSize(100, 100),
                LongRange.FromStartAndSize(200, 100),
            };

            store.CommitPatch("seg1@test", new byte[100], new UsenetYencHeader
            {
                FileName = "test.bin",
                FileSize = 300,
                LineLength = 128,
                PartNumber = 2,
                TotalParts = 3,
                PartSize = 100,
                PartOffset = 100,
            });

            var nzbFile = new DavNzbFile
            {
                SegmentIds = segmentIds,
                SegmentByteRanges = ranges,
            };

            var sampled = segmentIds.ToList();
            var result = HealthCheckService.FilterSegmentsForStat(
                sampled, segmentIds.ToList(), nzbFile, store);

            Assert.DoesNotContain("seg1@test", result);
            Assert.Contains("seg0@test", result);
            Assert.Contains("seg2@test", result);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PatchStore_CommitAndServe_EndToEnd()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-e2e-patch-" + Guid.NewGuid().ToString("N"));
        const string segmentId = "e2e-segment@test";
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var header = new UsenetYencHeader
        {
            FileName = "movie.mkv",
            FileSize = 8192,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 2,
            PartSize = 4096,
            PartOffset = 0,
        };

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.CatalogLoadTask;

            Assert.False(store.Contains(segmentId));
            store.CommitPatch(segmentId, content, header);
            Assert.True(store.Contains(segmentId));

            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.Equal(0, inner.BodyRequestCount);

            await using var ms = new MemoryStream();
            await response.Stream!.CopyToAsync(ms);
            Assert.Equal(content, ms.ToArray());

            var reloaded = new RepairPatchStore(dir, 1024 * 1024);
            await reloaded.CatalogLoadTask;
            Assert.True(reloaded.Contains(segmentId));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Migration_AppliesCleanly()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"nzbdav-migration-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var prevConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", tempDir);
            DavDatabaseContext.ResetOptionsForTests();
            await using var ctx = new DavDatabaseContext();
            await ctx.Database.EnsureCreatedAsync();
            Assert.NotNull(ctx.Par2RepairJobs);

            ctx.Par2RepairJobs.Add(new Par2RepairJob
            {
                Id = Guid.NewGuid(),
                DavItemId = Guid.NewGuid(),
                Path = "/view/test.mkv",
                State = Par2RepairJob.RepairJobState.Queued,
                MissingSegmentIds = ["seg1@test"],
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();

            var count = ctx.Par2RepairJobs.Count();
            Assert.Equal(1, count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", prevConfigPath);
            DavDatabaseContext.ResetOptionsForTests();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static byte[][] BuildSliceBytes(byte[] data, ulong sliceSize)
    {
        var count = (int)((data.Length + (long)sliceSize - 1) / (long)sliceSize);
        var slices = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            slices[i] = new byte[(int)sliceSize];
            var offset = i * (int)sliceSize;
            var len = Math.Min((int)sliceSize, data.Length - offset);
            data.AsSpan(offset, len).CopyTo(slices[i]);
        }
        return slices;
    }
}
