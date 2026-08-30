using System.Text;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Services.Repair;

[Collection(nameof(ConfigPathCollection))]
public sealed class Par2RepairServiceCorruptSourceTests : IAsyncLifetime
{
    private const int SliceSize = 4096;

    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-par2-corrupt-src-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private ConfigManager _config = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);
        DavDatabaseContext.ResetOptionsForTests();
        await using (var context = new DavDatabaseContext())
            await context.Database.MigrateAsync();

        _config = new ConfigManager();
        _config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.RepairPar2Enabled, ConfigValue = "true" },
        ]);
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        DavDatabaseContext.ResetOptionsForTests();
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ReconcileInterruptedJobs_ReleasesQueuedAndCoolsDownRunningJobs()
    {
        var patchDir = Path.Join(_configRoot, "reconcile-patches");
        var store = new RepairPatchStore(patchDir, 1024 * 1024);
        await store.EnsureCatalogLoadedAsync(CancellationToken.None);
        var service = new Par2RepairService(_config, null!, store);
        var runningId = Guid.NewGuid();
        var queuedId = Guid.NewGuid();

        await using (var context = new DavDatabaseContext())
        {
            context.Par2RepairJobs.AddRange(
                new Par2RepairJob
                {
                    Id = Guid.NewGuid(),
                    DavItemId = runningId,
                    Path = "/content/running.mkv",
                    State = Par2RepairJob.RepairJobState.Running,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
                    Attempts = 1,
                },
                new Par2RepairJob
                {
                    Id = Guid.NewGuid(),
                    DavItemId = queuedId,
                    Path = "/content/queued.mkv",
                    State = Par2RepairJob.RepairJobState.Queued,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    Attempts = 1,
                });
            await context.SaveChangesAsync();
        }

        await service.ReconcileInterruptedJobsForTestsAsync(CancellationToken.None);

        await using var verify = new DavDatabaseContext();
        var jobs = await verify.Par2RepairJobs.ToDictionaryAsync(job => job.DavItemId);
        var running = jobs[runningId];
        var queued = jobs[queuedId];
        Assert.Equal(Par2RepairJob.RepairJobState.Failed, running.State);
        Assert.NotNull(running.CompletedAt);
        Assert.NotNull(running.NextAttemptAt);
        Assert.Contains("interrupted by a backend restart", running.FailureReason, StringComparison.Ordinal);
        Assert.Equal(Par2RepairJob.RepairJobState.Failed, queued.State);
        Assert.NotNull(queued.CompletedAt);
        Assert.Null(queued.NextAttemptAt);
        Assert.Contains("queued when the backend restarted", queued.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneKnownCorruptTarget_ReconstructsAndCommits_WithoutRefetchingTheTarget()
    {
        var fileData = PatternBytes(SliceSize * 3, 0x11);
        await using var release = await SeedAsync(fileData, EqualSegments(3), recoveryExponents: [0u, 1u],
            corruptOnRead: [0]);

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item, [release.ContentSegmentIds[0]], CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, ok);
        Assert.True(release.Store.Contains(release.ContentSegmentIds[0]));
        Assert.False(release.Fake.BodyRequestCounts.ContainsKey(release.ContentSegmentIds[0]));
        Assert.Equal(
            fileData.AsSpan(0, SliceSize).ToArray(),
            await ReadPatchAsync(release.Store, release.ContentSegmentIds[0]));
    }

    [Fact]
    public async Task SequentialRepairPasses_RefetchPresentSourceInsteadOfRetainingWholeTarget()
    {
        var fileData = PatternBytes(SliceSize * 3, 0x12);
        await using var release = await SeedAsync(fileData, EqualSegments(3), recoveryExponents: [0u],
            corruptOnRead: [0]);

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item, [release.ContentSegmentIds[0]], CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, ok);
        // Discovery, Reed-Solomon reduction, and whole-file MD5 are independent
        // sequential passes. The test client has no disk segment cache, proving
        // source bytes are discarded rather than retained for the lifetime of repair.
        Assert.True(release.Fake.BodyRequestCounts[release.ContentSegmentIds[1]] >= 3);
        Assert.True(release.Fake.BodyRequestCounts[release.ContentSegmentIds[2]] >= 3);
    }

    [Fact]
    public async Task MissingTargetPlusDiscoveredAdjacentCorrupt_ReconstructsBoth()
    {
        var fileData = PatternBytes(SliceSize * 3, 0x22);
        await using var release = await SeedAsync(fileData, EqualSegments(3), recoveryExponents: [0u, 1u],
            omitFromProvider: [0], corruptOnRead: [1]);

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item, [release.ContentSegmentIds[0]], CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, ok);
        Assert.True(release.Store.Contains(release.ContentSegmentIds[0]));
        Assert.True(release.Store.Contains(release.ContentSegmentIds[1]));
        Assert.Equal(
            fileData.AsSpan(0, SliceSize).ToArray(),
            await ReadPatchAsync(release.Store, release.ContentSegmentIds[0]));
        Assert.Equal(
            fileData.AsSpan(SliceSize, SliceSize).ToArray(),
            await ReadPatchAsync(release.Store, release.ContentSegmentIds[1]));

        var blob = await ReadBlobAsync(release.Item.Id);
        Assert.Contains(1, blob.CorruptSegmentIndices ?? []);
    }

    [Fact]
    public async Task TwoKnownCorruptSegments_RequireTwoRecoverySlices()
    {
        var fileData = PatternBytes(SliceSize * 4, 0x33);
        await using var release = await SeedAsync(fileData, EqualSegments(4), recoveryExponents: [0u, 1u],
            corruptOnRead: [0, 2]);

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item,
            [release.ContentSegmentIds[0], release.ContentSegmentIds[2]],
            CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, ok);
        Assert.True(release.Store.Contains(release.ContentSegmentIds[0]));
        Assert.True(release.Store.Contains(release.ContentSegmentIds[2]));
        Assert.False(release.Store.Contains(release.ContentSegmentIds[1]));
    }

    [Fact]
    public async Task MisalignedSegmentAndSliceBoundaries_ReconstructsOverlappingSegments()
    {
        var fileData = PatternBytes(SliceSize * 2, 0x44);
        int[] sizes = [3000, 3000, SliceSize * 2 - 6000];
        await using var release = await SeedAsync(fileData, sizes, recoveryExponents: [0u, 1u],
            corruptOnRead: [0]);

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item, [release.ContentSegmentIds[0]], CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, ok);
        Assert.True(release.Store.Contains(release.ContentSegmentIds[0]));
        Assert.True(release.Store.Contains(release.ContentSegmentIds[1]));
        Assert.Equal(Slice(fileData, 0, sizes[0]), await ReadPatchAsync(release.Store, release.ContentSegmentIds[0]));
        Assert.Equal(Slice(fileData, sizes[0], sizes[1]), await ReadPatchAsync(release.Store, release.ContentSegmentIds[1]));
    }

    [Fact]
    public async Task SliceSpanningTwoNzbSegments_AssemblesPresentSliceAndRepairsTheOther()
    {
        var fileData = PatternBytes(SliceSize * 2, 0x55);
        int[] sizes = [2500, 2500, SliceSize * 2 - 5000];
        await using var release = await SeedAsync(fileData, sizes, recoveryExponents: [0u],
            corruptOnRead: [2]);

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item, [release.ContentSegmentIds[2]], CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, ok);
        Assert.True(release.Store.Contains(release.ContentSegmentIds[2]));
        Assert.Equal(
            Slice(fileData, sizes[0] + sizes[1], sizes[2]),
            await ReadPatchAsync(release.Store, release.ContentSegmentIds[2]));
    }

    [Fact]
    public async Task TargetIsSecondFileInMultiFilePar2Set_UsesGlobalSliceBase()
    {
        var first = PatternBytes(SliceSize * 2, 0x61);
        var target = PatternBytes(SliceSize * 2, 0x62);
        await using var release = await SeedAsync(
            target,
            EqualSegments(2),
            recoveryExponents: [0u, 1u],
            corruptOnRead: [0],
            extraFiles: [("other.bin", first, EqualSegments(2))]);

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item, [release.ContentSegmentIds[0]], CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, ok);
        Assert.True(release.Store.Contains(release.ContentSegmentIds[0]));
        Assert.Equal(
            target.AsSpan(0, SliceSize).ToArray(),
            await ReadPatchAsync(release.Store, release.ContentSegmentIds[0]));
        var siblingBodies = release.Fake.BodyRequestCounts.Keys
            .Where(id => id.Contains("extra-", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, siblingBodies.Count);
        Assert.All(siblingBodies, id => Assert.Equal(1, release.Fake.BodyRequestCounts[id]));
    }

    [Fact]
    public async Task SourceStreamOpensThenThrowsDuringCopyToAsync_IsTreatedAsCorruptInput()
    {
        var fileData = PatternBytes(SliceSize * 3, 0x66);
        await using var release = await SeedAsync(fileData, EqualSegments(3), recoveryExponents: [0u, 1u],
            omitFromProvider: [0], corruptOnRead: [1]);

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item, [release.ContentSegmentIds[0]], CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, ok);
        Assert.Equal(1, release.Fake.BodyRequestCounts[release.ContentSegmentIds[1]]);
        Assert.Equal(1, release.Fake.CompletionCallbackCounts[release.ContentSegmentIds[1]]);
        Assert.True(release.Store.Contains(release.ContentSegmentIds[1]));
    }

    [Fact]
    public async Task CancellationDuringDiscovery_PropagatesWithoutDamageOrPatch()
    {
        var fileData = PatternBytes(SliceSize * 3, 0x77);
        using var cts = new CancellationTokenSource();
        await using var release = await SeedAsync(fileData, EqualSegments(3), recoveryExponents: [0u, 1u],
            corruptOnRead: [0],
            cancelOnRead: [1],
            cancel: cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            release.Service.TryPar2RepairAsync(release.Item, [release.ContentSegmentIds[0]], cts.Token));

        Assert.False(release.Store.Contains(release.ContentSegmentIds[0]));
        Assert.False(release.Store.Contains(release.ContentSegmentIds[1]));
        var blob = await ReadBlobAsync(release.Item.Id);
        Assert.Null(blob.MissingSegmentIndices);
        Assert.Null(blob.CorruptSegmentIndices);
    }

    [Fact]
    public async Task BackgroundRepair_UrgentCallJoinsFlight_AndCanceledJoinerDoesNotCancelOwner()
    {
        var fileData = PatternBytes(SliceSize * 3, 0x78);
        var sourceReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSourceRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var release = await SeedAsync(
            fileData,
            EqualSegments(3),
            recoveryExponents: [0u],
            corruptOnRead: [0],
            sourceReadStarted: sourceReadStarted,
            allowSourceRead: allowSourceRead.Task);

        await release.Service.StartAsync(CancellationToken.None);
        try
        {
            await release.Service.EnqueueAsync(
                release.Item,
                [release.ContentSegmentIds[0]],
                CancellationToken.None);
            await sourceReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            using var canceledJoinerCts = new CancellationTokenSource();
            var canceledJoiner = release.Service.TryPar2RepairAsync(
                release.Item,
                [release.ContentSegmentIds[0]],
                canceledJoinerCts.Token);
            canceledJoinerCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledJoiner);

            var successfulJoiner = release.Service.TryPar2RepairAsync(
                release.Item,
                [release.ContentSegmentIds[0]],
                CancellationToken.None);
            Assert.False(successfulJoiner.IsCompleted);

            allowSourceRead.TrySetResult(true);
            Assert.Equal(
                Par2RepairOutcome.Repaired,
                await successfulJoiner.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(release.Store.Contains(release.ContentSegmentIds[0]));
            Assert.Equal(1, release.Service.GetDiagnosticSnapshot().TotalSucceeded);

            var job = await ReadJobAsync(release.Item.Id);
            Assert.Equal(1, job.Attempts);
            Assert.Equal(Par2RepairJob.RepairJobState.Succeeded, job.State);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await release.Service.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task InsufficientRecoverySlices_IsInfeasibleAndCommitsNothing()
    {
        var fileData = PatternBytes(SliceSize * 3, 0x88);
        await using var release = await SeedAsync(fileData, EqualSegments(3), recoveryExponents: [0u],
            corruptOnRead: [0, 1]);

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item,
            [release.ContentSegmentIds[0], release.ContentSegmentIds[1]],
            CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.NotRepaired, ok);
        Assert.False(release.Store.Contains(release.ContentSegmentIds[0]));
        Assert.False(release.Store.Contains(release.ContentSegmentIds[1]));
        var job = await ReadJobAsync(release.Item.Id);
        Assert.Equal(Par2RepairJob.RepairJobState.Infeasible, job.State);
        Assert.Contains("recovery slices", job.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TargetCountCap_ExceededBeforeRecoveryAllocation()
    {
        var fileData = PatternBytes(SliceSize * 3, 0x99);
        await using var release = await SeedAsync(fileData, EqualSegments(3), recoveryExponents: [0u, 1u],
            corruptOnRead: [0, 1], maxMissingSlices: "1");

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item,
            [release.ContentSegmentIds[0], release.ContentSegmentIds[1]],
            CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.NotRepaired, ok);
        Assert.False(release.Store.Contains(release.ContentSegmentIds[0]));
        var job = await ReadJobAsync(release.Item.Id);
        Assert.Equal(Par2RepairJob.RepairJobState.Infeasible, job.State);
        Assert.Contains("exceeds cap", job.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongWholeFileMd5_RejectsStagedResultAndCommitsNothing()
    {
        var fileData = PatternBytes(SliceSize * 2, 0xAA);
        var wrongHash = new byte[16];
        Array.Fill(wrongHash, (byte)0xAB);
        await using var release = await SeedAsync(fileData, EqualSegments(2), recoveryExponents: [0u],
            corruptOnRead: [0], fileHashOverride: wrongHash);

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item, [release.ContentSegmentIds[0]], CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.NotRepaired, ok);
        Assert.False(release.Store.Contains(release.ContentSegmentIds[0]));
        var job = await ReadJobAsync(release.Item.Id);
        Assert.Equal(Par2RepairJob.RepairJobState.Failed, job.State);
        Assert.Contains("Whole-file MD5", job.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulBodyOpens_FireCompletionCallbackExactlyOnce()
    {
        var fileData = PatternBytes(SliceSize * 3, 0xBB);
        await using var release = await SeedAsync(fileData, EqualSegments(3), recoveryExponents: [0u],
            corruptOnRead: [0]);

        var ok = await release.Service.TryPar2RepairAsync(
            release.Item, [release.ContentSegmentIds[0]], CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, ok);
        Assert.Equal(release.Fake.BodyRequestCount, release.Fake.CompletionCallbackCount);
        foreach (var (id, count) in release.Fake.BodyRequestCounts)
            Assert.Equal(count, release.Fake.CompletionCallbackCounts.GetValueOrDefault(id));
    }

    [Fact]
    public async Task VerifyAll_CleanFileAboveCap_ReturnsVerifiedClean()
    {
        var fileData = PatternBytes(SliceSize * 3, 0xBC);
        await using var release = await SeedAsync(
            fileData, EqualSegments(3), recoveryExponents: [0u], maxMissingSlices: "1");

        var outcome = await release.Service.TryPar2RepairAsync(
            release.Item, null, CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.VerifiedClean, outcome);
        var job = await ReadJobAsync(release.Item.Id);
        Assert.Equal(Par2RepairJob.RepairJobState.Succeeded, job.State);
        Assert.Equal(0, job.SlicesReconstructed);
        Assert.False(release.Store.Contains(release.ContentSegmentIds[0]));
        Assert.False(release.Store.Contains(release.ContentSegmentIds[1]));
        Assert.False(release.Store.Contains(release.ContentSegmentIds[2]));
    }

    [Fact]
    public async Task VerifyAll_DamageAboveCap_UsesActualDamagedSliceCount()
    {
        var fileData = PatternBytes(SliceSize * 3, 0xBD);
        await using var release = await SeedAsync(
            fileData, EqualSegments(3), recoveryExponents: [0u, 1u],
            corruptOnRead: [0, 1], maxMissingSlices: "1");

        var outcome = await release.Service.TryPar2RepairAsync(
            release.Item, null, CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.NotRepaired, outcome);
        var job = await ReadJobAsync(release.Item.Id);
        Assert.Equal(Par2RepairJob.RepairJobState.Infeasible, job.State);
        Assert.Equal("Missing slice count 2 exceeds cap 1.", job.FailureReason);
    }

    [Fact]
    public async Task VerifyAll_DamageWithinCap_ReconstructsOnlyDamagedSegment()
    {
        var fileData = PatternBytes(SliceSize * 3, 0xBE);
        await using var release = await SeedAsync(
            fileData, EqualSegments(3), recoveryExponents: [0u, 1u],
            corruptOnRead: [0], maxMissingSlices: "1");

        var outcome = await release.Service.TryPar2RepairAsync(
            release.Item, null, CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, outcome);
        Assert.True(release.Store.Contains(release.ContentSegmentIds[0]));
        Assert.False(release.Store.Contains(release.ContentSegmentIds[1]));
        Assert.False(release.Store.Contains(release.ContentSegmentIds[2]));
    }

    private async Task<SeededRelease> SeedAsync(
        byte[] targetData,
        int[] targetSegmentSizes,
        uint[] recoveryExponents,
        int[]? omitFromProvider = null,
        int[]? corruptOnRead = null,
        int[]? cancelOnRead = null,
        CancellationTokenSource? cancel = null,
        byte[]? fileHashOverride = null,
        IReadOnlyList<(string FileName, byte[] Data, int[] Sizes)>? extraFiles = null,
        string? maxMissingSlices = null,
        TaskCompletionSource<bool>? sourceReadStarted = null,
        Task? allowSourceRead = null)
    {
        _config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.RepairPar2Enabled, ConfigValue = "true" },
            new ConfigItem
            {
                ConfigName = ConfigKeys.RepairPar2MaxMissingSlices,
                ConfigValue = maxMissingSlices ?? "8",
            },
        ]);
        omitFromProvider ??= [];
        corruptOnRead ??= [];
        cancelOnRead ??= [];
        extraFiles ??= [];

        var token = Guid.NewGuid().ToString("N")[..8];
        var targetName = $"target-{token}.bin";
        var files = extraFiles
            .Select(file => (file.FileName, file.Data))
            .Append((targetName, targetData))
            .ToList();
        var hashOverrides = fileHashOverride is null
            ? null
            : new Dictionary<string, byte[]>(StringComparer.Ordinal) { [targetName] = fileHashOverride };
        var (indexBytes, volumeBytes) = Par2TestEncoder.EncodeSet(
            files, SliceSize, recoveryExponents, hashOverrides);

        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var rangesById = new Dictionary<string, LongRange>(StringComparer.Ordinal);
        var nzbFiles = new List<(string Name, List<(string Id, int Bytes)> Segments)>();

        foreach (var extra in extraFiles)
        {
            var (ids, ranges, parts) = Split(extra.Data, extra.Sizes, $"extra-{token}");
            nzbFiles.Add((extra.FileName, ids.Zip(extra.Sizes, (id, size) => (id, size)).ToList()));
            AddPayloads(payloads, rangesById, ids, parts, ranges);
        }

        var (contentIds, contentRanges, contentParts) = Split(targetData, targetSegmentSizes, $"tgt-{token}");
        nzbFiles.Add((targetName, contentIds.Zip(targetSegmentSizes, (id, size) => (id, size)).ToList()));
        AddPayloads(payloads, rangesById, contentIds, contentParts, contentRanges);

        var indexId = $"aaa-index-{token}@test";
        var volumeId = $"zzz-vol-{token}@test";
        payloads[indexId] = indexBytes;
        payloads[volumeId] = volumeBytes;
        rangesById[indexId] = LongRange.FromStartAndSize(0, indexBytes.Length);
        rangesById[volumeId] = LongRange.FromStartAndSize(0, volumeBytes.Length);
        nzbFiles.Add(($"{targetName}.par2", [(indexId, indexBytes.Length)]));
        nzbFiles.Add(($"{targetName}.vol00+{recoveryExponents.Length:00}.par2", [(volumeId, volumeBytes.Length)]));

        var omit = omitFromProvider.Select(i => contentIds[i]).ToHashSet(StringComparer.Ordinal);
        foreach (var id in omit)
            payloads.Remove(id);

        var corruptIds = corruptOnRead.Select(i => contentIds[i]).ToHashSet(StringComparer.Ordinal);
        var cancelIds = cancelOnRead.Select(i => contentIds[i]).ToHashSet(StringComparer.Ordinal);

        var fake = new FakeNntpClient(
            payloads,
            useCachedYencStreams: true,
            segmentRanges: rangesById,
            decodedStreamFactory: (id, bytes) =>
            {
                if (cancelIds.Contains(id))
                    return new CancelOnReadStream(cancel ?? throw new InvalidOperationException("cancel CTS required"));
                if (corruptIds.Contains(id))
                    return new ThrowingReadStream(id);
                if (sourceReadStarted is not null
                    && allowSourceRead is not null
                    && contentIds.Contains(id, StringComparer.Ordinal))
                    return new GateOnFirstReadStream(bytes, sourceReadStarted, allowSourceRead);
                return new MemoryStream(bytes, writable: false);
            });

        var nzbXml = BuildNzbXml(nzbFiles);
        var nzbBlobId = Guid.NewGuid();
        await using (var nzbStream = new MemoryStream(Encoding.UTF8.GetBytes(nzbXml)))
            await BlobStore.WriteBlob(nzbBlobId, nzbStream);

        var fileBlobId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await BlobStore.WriteBlob(fileBlobId, new DavNzbFile
        {
            Id = itemId,
            SegmentIds = contentIds,
            SegmentByteRanges = contentRanges,
        });

        var item = DavItem.New(
            itemId,
            DavItem.ContentFolder,
            targetName,
            fileSize: targetData.Length,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            releaseDate: DateTimeOffset.UtcNow.AddDays(-1),
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: fileBlobId,
            nzbBlobId: nzbBlobId);

        await using (var context = new DavDatabaseContext())
        {
            context.Items.Add(item);
            await context.SaveChangesAsync();
        }

        var patchDir = Path.Join(_configRoot, "patches", token);
        var store = new RepairPatchStore(patchDir, 32 * 1024 * 1024);
        await store.EnsureCatalogLoadedAsync(CancellationToken.None);
        var usenet = new UsenetStreamingClient(fake, store);
        var service = new Par2RepairService(_config, usenet, store);
        return new SeededRelease(item, contentIds, fake, store, service, usenet);
    }

    private static int[] EqualSegments(int count) => Enumerable.Repeat(SliceSize, count).ToArray();

    private static byte[] PatternBytes(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = (byte)(i * 7 + seed);
        return data;
    }

    private static byte[] Slice(byte[] data, int start, int count) => data.AsSpan(start, count).ToArray();

    private static (string[] Ids, LongRange[] Ranges, byte[][] Parts) Split(
        byte[] data, int[] sizes, string prefix)
    {
        var ids = new string[sizes.Length];
        var ranges = new LongRange[sizes.Length];
        var parts = new byte[sizes.Length][];
        var offset = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            ids[i] = $"{prefix}-{i}@test";
            ranges[i] = LongRange.FromStartAndSize(offset, sizes[i]);
            parts[i] = data.AsSpan(offset, sizes[i]).ToArray();
            offset += sizes[i];
        }

        if (offset != data.Length)
            throw new ArgumentException("Segment sizes must cover the file.");
        return (ids, ranges, parts);
    }

    private static void AddPayloads(
        Dictionary<string, byte[]> payloads,
        Dictionary<string, LongRange> rangesById,
        string[] ids,
        byte[][] parts,
        LongRange[] ranges)
    {
        for (var i = 0; i < ids.Length; i++)
        {
            payloads[ids[i]] = parts[i];
            rangesById[ids[i]] = ranges[i];
        }
    }

    private static string BuildNzbXml(IReadOnlyList<(string Name, List<(string Id, int Bytes)> Segments)> files)
    {
        var xml = new StringBuilder();
        xml.Append("""<?xml version="1.0" encoding="utf-8"?><nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">""");
        foreach (var (name, segments) in files)
        {
            xml.Append("<file subject=\"&quot;").Append(name).Append("&quot; yEnc\">");
            xml.Append("<segments>");
            for (var i = 0; i < segments.Count; i++)
            {
                xml.Append("<segment bytes=\"").Append(segments[i].Bytes).Append("\" number=\"")
                    .Append(i + 1).Append("\">").Append(segments[i].Id).Append("</segment>");
            }

            xml.Append("</segments></file>");
        }

        xml.Append("</nzb>");
        return xml.ToString();
    }

    private static async Task<byte[]> ReadPatchAsync(RepairPatchStore store, string segmentId)
    {
        Assert.True(store.TryGet(segmentId, out var response));
        await using var output = new MemoryStream();
        await response!.Stream!.CopyToAsync(output);
        return output.ToArray();
    }

    private static async Task<DavNzbFile> ReadBlobAsync(Guid itemId)
    {
        await using var context = new DavDatabaseContext();
        var item = await context.Items.AsNoTracking().SingleAsync(x => x.Id == itemId);
        return (await BlobStore.ReadBlob<DavNzbFile>(item.FileBlobId!.Value))!;
    }

    private static async Task<Par2RepairJob> ReadJobAsync(Guid itemId)
    {
        await using var context = new DavDatabaseContext();
        return await context.Par2RepairJobs.SingleAsync(x => x.DavItemId == itemId);
    }

    private sealed class SeededRelease(
        DavItem item,
        string[] contentSegmentIds,
        FakeNntpClient fake,
        RepairPatchStore store,
        Par2RepairService service,
        UsenetStreamingClient usenet) : IAsyncDisposable
    {
        public DavItem Item { get; } = item;
        public string[] ContentSegmentIds { get; } = contentSegmentIds;
        public FakeNntpClient Fake { get; } = fake;
        public RepairPatchStore Store { get; } = store;
        public Par2RepairService Service { get; } = service;

        public ValueTask DisposeAsync()
        {
            usenet.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingReadStream(string segmentId) : MemoryStream(new byte[64])
    {
        private UsenetCorruptArticleException CreateException() =>
            new(segmentId, "provider-a", new InvalidDataException("CRC mismatch"));

        public override int Read(byte[] buffer, int offset, int count) =>
            throw CreateException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(CreateException());
    }

    private sealed class CancelOnReadStream(CancellationTokenSource cts) : MemoryStream([1, 2, 3, 4])
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            return ValueTask.FromException<int>(new OperationCanceledException(cts.Token));
        }
    }

    private sealed class GateOnFirstReadStream(
        byte[] buffer,
        TaskCompletionSource<bool> started,
        Task release) : MemoryStream(buffer, writable: false)
    {
        private int _hasWaited;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _hasWaited, 1) == 0)
            {
                started.TrySetResult(true);
                await release.WaitAsync(cancellationToken);
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
