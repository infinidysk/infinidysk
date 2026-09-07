using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.Par2Recovery;

namespace NzbWebDAV.Tests.Services.Repair;

[Collection(nameof(ConfigPathCollection))]
public sealed class Par2RepairServiceMultipartTests : IAsyncLifetime
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "par2-multipart-" + Guid.NewGuid().ToString("N"));
    private string? _previous;
    private ConfigManager _config = null!;

    public async Task InitializeAsync()
    {
        _previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _root);
        DavDatabaseContext.ResetOptionsForTests();
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        _config = new ConfigManager();
        _config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.RepairPar2Enabled, ConfigValue = "true" },
        ]);
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previous);
        DavDatabaseContext.ResetOptionsForTests();
        Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(DavItem.ItemSubType.MultipartFile)]
    [InlineData(DavItem.ItemSubType.RarFile)]
    public async Task TwoDamagedVolumes_ReconstructOneUnionAndSurviveRestart(DavItem.ItemSubType subtype)
    {
        var first = Data(4096 * 6, "first");
        var second = Data(4096 * 7 + 3, "second");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("release.part01.rar", first, Sizes(first.Length), [4]),
            new("release.part02.rar", second, Sizes(second.Length), [5]),
        ], [0, 1, 2], subtype);
        var ids = new[] { release.Files[0].Ids[4], release.Files[1].Ids[5] };

        Assert.Equal(Par2RepairOutcome.Repaired, await release.Service.TryPar2RepairAsync(release.Item, ids, CancellationToken.None));
        var reloaded = new RepairPatchStore(release.PatchDirectory, release.Store.MaxBytes);
        await reloaded.EnsureCatalogLoadedAsync(CancellationToken.None);
        foreach (var (id, expected) in new[] { (ids[0], first.AsSpan(4096 * 4, 4096).ToArray()), (ids[1], second.AsSpan(4096 * 5, 4096).ToArray()) })
        {
            Assert.True(reloaded.HasUsablePatch(id));
            Assert.True(reloaded.TryGet(id, out var response));
            await using var stream = response!.Stream!;
            await using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(expected, copy.ToArray());
        }
        Assert.False(reloaded.Contains(release.Files[0].Ids[0]));
        await using var db = new DavDatabaseContext();
        var job = await db.Par2RepairJobs.SingleAsync();
        Assert.Equal(2, job.SlicesReconstructed);
        Assert.Equal(Par2RepairJob.RepairJobState.Succeeded, job.State);
    }

    private static int[] Sizes(int length)
        => Enumerable.Range(0, (length + 4095) / 4096).Select(index => Math.Min(4096, length - index * 4096)).ToArray();

    [Fact]
    public async Task CompletedUncoveredFlight_CannotKeepJoinerRetryingIndefinitely()
    {
        var data = Data(4096 * 3, "bounded-flight");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length)),
        ], [1], DavItem.ItemSubType.MultipartFile);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var flightType = typeof(Par2RepairService).GetNestedType("RepairFlight", System.Reflection.BindingFlags.NonPublic)!;
        var flight = Activator.CreateInstance(flightType, flags, null, new object?[] { Array.Empty<string>() }, null)!;
        ((TaskCompletionSource<Par2RepairOutcome>)flightType.GetProperty("Completion")!.GetValue(flight)!).SetResult(Par2RepairOutcome.VerifiedClean);
        ((TaskCompletionSource)flightType.GetProperty("Finished")!.GetValue(flight)!).SetResult();
        var flights = (System.Collections.IDictionary)typeof(Par2RepairService).GetField("_repairFlights", flags)!.GetValue(release.Service)!;
        flights.Add(release.Item.Id, flight);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var result = await Task.Run(() => release.Service.TryPar2RepairAsync(release.Item,
                [release.Files[0].Ids[0]], cancellation.Token), cancellation.Token);
            Assert.Equal(Par2RepairOutcome.NotRepaired, result);
            Assert.Equal(0, release.Fake.BodyRequestCount);
        }
        finally { flights.Clear(); }
    }

    [Theory]
    [InlineData(Par2RepairJob.RepairJobState.Queued)]
    [InlineData(Par2RepairJob.RepairJobState.Failed)]
    [InlineData(Par2RepairJob.RepairJobState.Infeasible)]
    public async Task ResumedTargetedJob_PreservesPreviouslyReportedArticles(Par2RepairJob.RepairJobState state)
    {
        var data = Data(4096 * 6, "resumed-ids");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length)),
        ], [1, 2], DavItem.ItemSubType.MultipartFile);
        var previousId = release.Files[0].Ids[4];
        var currentId = release.Files[0].Ids[5];
        await using (var context = new DavDatabaseContext())
        {
            context.Par2RepairJobs.Add(new Par2RepairJob
            {
                Id = Guid.NewGuid(), DavItemId = release.Item.Id, Path = release.Item.Path,
                State = state, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1), Attempts = 1,
                MissingSegmentIds = [previousId],
            });
            await context.SaveChangesAsync();
        }

        Assert.Equal(Par2RepairOutcome.Repaired, await release.Service.TryPar2RepairAsync(release.Item, [currentId], CancellationToken.None));
        await AssertPatchAsync(release, 0, 4);
        await AssertPatchAsync(release, 0, 5);
        var job = await ReadJobAsync();
        Assert.Equal(2, job.Attempts);
        Assert.Equal(new[] { previousId, currentId }.Order(), job.MissingSegmentIds.Order());
    }

    [Fact]
    public async Task MultipartStream_ReportsGapWithoutWaitingAndReadsValidatedPatchAfterRepair()
    {
        var data = Data(4096 * 6, "stream-trigger");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length), [4]),
        ], [1], DavItem.ItemSubType.MultipartFile);
        var payload = await BlobStore.ReadBlob<DavMultipartFile>(release.Item.FileBlobId!.Value);
        Assert.NotNull(payload);
        var previousSink = Par2RepairTriggerSink.Current;
        var previousReports = Par2RepairTriggerSink.TestReports;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release.Service.BeforePatchPublicationForTests = token => { entered.TrySetResult(); return proceed.Task.WaitAsync(token); };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var repaired = new RepairedSegmentNntpClient(release.Fake, release.Store);
        var workers = release.Service.RunWorkersAsync(cancellation.Token);
        Par2RepairTriggerSink.Current = new Par2RepairTriggerSink(release.Service);
        Par2RepairTriggerSink.TestReports = [];
        try
        {
            await using (var stream = new DavMultipartFileStream(payload, repaired, 0, null, false, release.Item.Path))
            await using (var output = new MemoryStream())
            {
                await stream.CopyToAsync(output, cancellation.Token);
                var fallback = data.ToArray();
                fallback.AsSpan(4096 * 4, 4096).Clear();
                Assert.Equal(fallback, output.ToArray());
            }
            await entered.Task.WaitAsync(cancellation.Token);
            Assert.Contains(Par2RepairTriggerSink.TestReports, report => report.Path == release.Item.Path && report.SegmentId == release.Files[0].Ids[4]);
            Assert.False(release.Store.HasUsablePatch(release.Files[0].Ids[4]));
            proceed.TrySetResult();
            await WaitForSuccessfulJobAsync(cancellation.Token);
            await using var restored = new DavMultipartFileStream(payload, repaired, 0, null, false, release.Item.Path);
            await using var complete = new MemoryStream();
            await restored.CopyToAsync(complete, cancellation.Token);
            Assert.Equal(data, complete.ToArray());
        }
        finally
        {
            proceed.TrySetResult();
            await cancellation.CancelAsync();
            try { await workers; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Par2RepairTriggerSink.Current = previousSink;
            Par2RepairTriggerSink.TestReports = previousReports;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvictedPatchAndCache_FallbackRetainsDownloadAdmission(bool cacheEnabled)
    {
        _config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxDownloadConnections, ConfigValue = "1" },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxQueueConnections, ConfigValue = "1" },
        ]);
        var id = "admitted-local@test";
        byte[] bytes = [1, 2, 3, 4];
        var header = new UsenetSharp.Models.UsenetYencHeader
        {
            FileName = "volume.rar", FileSize = bytes.Length, PartSize = bytes.Length,
            PartNumber = 1, TotalParts = 1, PartOffset = 0, LineLength = 128,
        };
        var patches = new RepairPatchStore(Path.Join(_root, "admission-patches"), 1024);
        await patches.EnsureCatalogLoadedAsync(CancellationToken.None);
        patches.CommitPatch(id, bytes, header);
        using var provider = new FakeNntpClient(new Dictionary<string, byte[]> { [id] = bytes }, useCachedYencStreams: true);
        using var downloading = new DownloadingNntpClient(provider, _config);
        var cacheDirectory = Path.Join(_root, "admission-cache");
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)));
        Directory.CreateDirectory(Path.Join(cacheDirectory, hash[..2]));
        var bodyPath = Path.Join(cacheDirectory, hash[..2], hash);
        await File.WriteAllBytesAsync(bodyPath, bytes);
        await File.WriteAllTextAsync(bodyPath + ".h", System.Text.Json.JsonSerializer.Serialize(header, SegmentCacheNntpClient.HeaderJsonOptions));
        using var cache = new SegmentCacheNntpClient(downloading, cacheDirectory, 1024);
        await cache.CatalogLoadTask;
        var cachedResponse = await cache.DecodedBodyAsync(id, CancellationToken.None);
        await using (var cachedStream = cachedResponse.Stream!)
        {
            var cachedHeader = await cachedStream.GetYencHeadersAsync();
            Assert.NotNull(cachedHeader);
            Assert.Equal(header.PartSize, cachedHeader.PartSize);
            Assert.Equal(header.FileName, cachedHeader.FileName);
        }
        Assert.Equal(0, provider.BodyRequestCount);
        using var repaired = new RepairedSegmentNntpClient(cacheEnabled ? cache : downloading, patches);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handle = await repaired.AcquireExclusiveConnectionAsync(id, cancellation.Token);
        Assert.NotNull(handle.OnConnectionReadyAgain);
        Assert.Equal(0, provider.BodyRequestCount);
        var other = repaired.AcquireExclusiveConnectionAsync("second@test", cancellation.Token);
        Assert.False(other.IsCompleted);
        patches.CommitPatch("replacement@test", new byte[1024], new UsenetSharp.Models.UsenetYencHeader
        {
            FileName = "replacement.rar", FileSize = 1024, PartSize = 1024,
            PartNumber = 1, TotalParts = 1, PartOffset = 0, LineLength = 128,
        });
        Assert.False(patches.Contains(id));
        File.Delete(bodyPath);
        File.Delete(bodyPath + ".h");
        var response = await repaired.DecodedBodyAsync(id, handle, cancellation.Token);
        await using var stream = response.Stream!;
        await using var output = new MemoryStream();
        await stream.CopyToAsync(output, cancellation.Token);
        Assert.Equal(bytes, output.ToArray());
        Assert.Equal(1, provider.BodyRequestCount);
        Assert.Equal(1, provider.CompletionCallbackCount);
        var next = await other;
        next.OnConnectionReadyAgain!(UsenetSharp.Models.ArticleBodyResult.Cancelled, "test-release");
    }

    [Fact]
    public async Task OversizedRelease_IsRejectedBeforeContentOrRecoveryReads()
    {
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RepairPar2MaxReleaseGb, ConfigValue = "1" }]);
        var data = Data(4096 * 6, "oversized-metadata");
        var parity = Par2TestEncoder.EncodeSet([("volume.rar", data)], 4096, [1u]);
        RewritePacket(parity.indexBytes, "PAR 2.0\0FileDesc"u8, body =>
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(48), 2UL * 1024 * 1024 * 1024));
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length), [4]),
        ], [], DavItem.ItemSubType.MultipartFile, parity: parity);
        Assert.Equal(Par2RepairOutcome.NotRepaired, await release.Service.TryPar2RepairAsync(release.Item,
            [release.Files[0].Ids[4]], CancellationToken.None));
        Assert.Contains("exceeds release cap", (await ReadJobAsync()).FailureReason, StringComparison.Ordinal);
        Assert.All(release.Fake.BodyRequestCounts.Keys, id => Assert.StartsWith("aaa-index-", id));
    }

    [Fact]
    public async Task RecoveryScan_RejectsOversizedCandidateBeforeBodyRead()
    {
        var data = Data(4096 * 6, "oversized-recovery");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length), [4]),
        ], [1], DavItem.ItemSubType.MultipartFile, recoveryFileSize: Par2RepairService.MaxPar2RecoveryScanBytes + 1);

        Assert.Equal(Par2RepairOutcome.NotRepaired, await release.Service.TryPar2RepairAsync(release.Item,
            [release.Files[0].Ids[4]], CancellationToken.None));

        Assert.Contains("recovery scan byte limit", (await ReadJobAsync()).FailureReason, StringComparison.Ordinal);
        var recoveryRead = Assert.Single(release.Fake.BodyRequestCounts, pair => pair.Key.StartsWith("zzz-volume-", StringComparison.Ordinal));
        Assert.Equal(1, recoveryRead.Value);
        Assert.Equal(0, release.Store.EntryCount);
        Assert.Equal(0, release.Service.GetDiagnosticSnapshot().AdmissionActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryScan_CountsForeignPacketsAcrossCandidates(bool fits)
    {
        var data = Data(4096 * 6, "recovery-budget");
        var parity = Par2TestEncoder.EncodeSet([("volume.rar", data)], 4096, [1u]);
        var foreign = Par2TestEncoder.EncodeSet([("foreign.bin", Data(4096, "foreign-recovery"))], 4096, [1u]);
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length), [4]),
        ], [], DavItem.ItemSubType.MultipartFile, parity: parity,
            additionalParity: [("foreign.vol00+01.par2", foreign.volumeBytes)]);
        release.Service.RecoveryScanByteLimitForTests = foreign.volumeBytes.Length + parity.indexBytes.Length + parity.volumeBytes.Length - (fits ? 0 : 1);
        var id = release.Files[0].Ids[4];

        Assert.Equal(fits ? Par2RepairOutcome.Repaired : Par2RepairOutcome.NotRepaired,
            await release.Service.TryPar2RepairAsync(release.Item, [id], CancellationToken.None));

        var foreignReads = Assert.Single(release.Fake.BodyRequestCounts, pair => pair.Key.StartsWith("000-extra-", StringComparison.Ordinal));
        Assert.Equal(2, foreignReads.Value);
        if (fits) await AssertPatchAsync(release, 0, 4);
        else
        {
            Assert.Contains("recovery scan byte limit", (await ReadJobAsync()).FailureReason, StringComparison.Ordinal);
            Assert.Equal(0, release.Store.EntryCount);
        }
        Assert.Equal(0, release.Service.GetDiagnosticSnapshot().AdmissionActive);
    }

    [Fact]
    public async Task SourceComparisonBudget_RejectsCartesianScanBeforeIdentityReads()
    {
        var count = (int)Math.Sqrt(Par2RepairService.MaxPar2SourceComparisons) + 1;
        var files = Enumerable.Range(0, count).Select(index => new Par2RepairTestReleaseBuilder.SourceFile(
            $"source-{index}.bin", Data(4096, $"source-{index}"), [4096])).ToArray();
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync(files, []);

        Assert.Equal(Par2RepairOutcome.NotRepaired, await release.Service.TryPar2RepairAsync(release.Item,
            [release.ContentSegmentIds[0]], CancellationToken.None));

        Assert.Contains("100,000-comparison limit", (await ReadJobAsync()).FailureReason, StringComparison.Ordinal);
        Assert.All(release.Fake.BodyRequestCounts.Keys, id => Assert.StartsWith("aaa-index-", id));
        Assert.Equal(0, release.Store.EntryCount);
        Assert.Equal(0, release.Service.GetDiagnosticSnapshot().AdmissionActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityProbeBudget_IsSharedAcrossCandidatesAndProbeKinds(bool requestLimit)
    {
        var data = Data(4096 * 3, "identity-target");
        var parity = Par2TestEncoder.EncodeSet([("protected.bin", Data(data.Length, "identity-protected"))], 4096, [1u]);
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("decoy.bin", Data(data.Length, "identity-decoy"), Sizes(data.Length)),
            new("target.bin", data, Sizes(data.Length), [0]),
        ], [], parity: parity);
        if (requestLimit) release.Service.IdentityRequestLimitForTests = 11;
        else release.Service.IdentityByteLimitForTests = 50_000;

        Assert.Equal(Par2RepairOutcome.NotRepaired, await release.Service.TryPar2RepairAsync(release.Item,
            [release.ContentSegmentIds[0]], CancellationToken.None));

        Assert.Contains(requestLimit ? "identity discovery exceeds its request limit" : "identity discovery exceeds its byte limit",
            (await ReadJobAsync()).FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, release.Store.EntryCount);
        Assert.Equal(0, release.Service.GetDiagnosticSnapshot().AdmissionActive);
        Assert.Contains(release.Files[0].Ids[1], release.Fake.BodyRequestCounts.Keys);
        Assert.Contains(release.Files[1].Ids[2], release.Fake.BodyRequestCounts.Keys);
    }

    [Fact]
    public async Task IdentityProbes_ReuseAnArticleAcrossSliceProofs()
    {
        var data = Data(4096 * 8, "identity-reuse");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, [16384, 16384], [0]),
        ], [1, 2, 3, 4], DavItem.ItemSubType.MultipartFile);

        Assert.Equal(Par2RepairOutcome.Repaired, await release.Service.TryPar2RepairAsync(release.Item,
            [release.Files[0].Ids[0]], CancellationToken.None));

        Assert.Equal(5, release.Fake.BodyRequestCounts[release.Files[0].Ids[1]]);
        await AssertPatchAsync(release, 0, 0);
    }

    [Fact]
    public async Task IdentityProbes_AllowMatchingSlicesBeyondEarlyPositions()
    {
        var protectedData = Data(4096 * 72, "late-proof-original");
        var postedData = Data(protectedData.Length, "late-proof-posted");
        protectedData.AsSpan(4096 * 70, 4096).CopyTo(postedData.AsSpan(4096 * 70));
        var parity = Par2TestEncoder.EncodeSet([("volume.rar", protectedData)], 4096, [1u]);
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RepairPar2MaxMissingSlices, ConfigValue = "1" }]);
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", postedData, Sizes(postedData.Length), [0]),
        ], [], DavItem.ItemSubType.MultipartFile, parity: parity);

        Assert.Equal(Par2RepairOutcome.NotRepaired, await release.Service.TryPar2RepairAsync(release.Item,
            [release.Files[0].Ids[0]], CancellationToken.None));
        Assert.Contains("Missing slice count", (await ReadJobAsync()).FailureReason, StringComparison.Ordinal);
        Assert.Contains(release.Files[0].Ids[70], release.Fake.BodyRequestCounts.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataSearch_StopsAtNamedAndMagicCandidateLimits(bool named)
    {
        var count = named ? Par2RepairService.MaxPar2MetadataCandidates : Par2RepairService.MaxPar2MagicCandidates;
        var extras = Enumerable.Range(0, count + 1)
            .Select(index => (Name: named ? $"candidate-{index}.par2" : $"candidate-{index}", Bytes: new byte[64])).ToArray();
        var data = Data(4096 * 3, "bounded-search");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length), [0]),
        ], [], DavItem.ItemSubType.MultipartFile, parity: ([], []), obfuscatedParity: !named, additionalParity: extras);
        Assert.Equal(Par2RepairOutcome.NotRepaired, await release.Service.TryPar2RepairAsync(release.Item,
            [release.Files[0].Ids[0]], CancellationToken.None));
        Assert.Contains($"{count}-candidate limit", (await ReadJobAsync()).FailureReason, StringComparison.Ordinal);
        Assert.Equal(count, release.Fake.BodyRequestCounts.Keys.Count(id => id.StartsWith("000-extra-", StringComparison.Ordinal)));
    }

    private static void RewritePacket(byte[] packets, ReadOnlySpan<byte> packetType, Action<byte[]> rewriteBody)
    {
        for (var offset = 0; offset < packets.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(packets.AsSpan(offset + 8)));
            if (packets.AsSpan(offset + 48, 16).SequenceEqual(packetType))
            {
                var body = packets.AsSpan(offset + 64, length - 64).ToArray();
                rewriteBody(body);
                body.CopyTo(packets, offset + 64);
#pragma warning disable CA5351
                MD5.HashData(packets.AsSpan(offset + 32, length - 32)).CopyTo(packets.AsSpan(offset + 16, 16));
#pragma warning restore CA5351
                return;
            }
            offset += length;
        }
        throw new InvalidDataException("Expected fixture packet is missing.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentSets_FilterBeforeExponentDedupAndRejectCrossSetRequests(bool crossSet)
    {
        var first = Data(4096 * 6, "independent-first");
        var second = Data(4096 * 7, "independent-second");
        var main = Par2TestEncoder.EncodeSet([("first.rar", first)], 4096, [0u]);
        var foreign = Par2TestEncoder.EncodeSet([("second.rar", second)], 4096, [0u]);
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("first.rar", first, Sizes(first.Length), [4]),
            new("second.rar", second, Sizes(second.Length), crossSet ? [5] : []),
        ], [], DavItem.ItemSubType.MultipartFile, parity: main,
            additionalParity: [("foreign.par2", foreign.indexBytes), ("foreign.vol00+01.par2", foreign.volumeBytes)]);
        var ids = crossSet ? new[] { release.Files[0].Ids[4], release.Files[1].Ids[5] } : [release.Files[0].Ids[4]];
        var result = await release.Service.TryPar2RepairAsync(release.Item, ids, CancellationToken.None);
        Assert.Equal(crossSet ? Par2RepairOutcome.NotRepaired : Par2RepairOutcome.Repaired, result);
        if (crossSet)
        {
            Assert.All(ids, id => Assert.False(release.Store.HasUsablePatch(id)));
            Assert.Contains("cross-set repair is not supported", (await ReadJobAsync()).FailureReason, StringComparison.Ordinal);
        }
        else await AssertPatchAsync(release, 0, 4);
    }

    [Fact]
    public async Task ConflictingCriticalMetadata_IsRejectedBeforePublication()
    {
        var data = Data(4096 * 6, "critical-conflict");
        var good = Par2TestEncoder.EncodeSet([("volume.rar", data)], 4096, [1u]);
        var conflicting = good.indexBytes.ToArray();
        for (var offset = 0; offset < conflicting.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(conflicting.AsSpan(offset + 8)));
            if (conflicting.AsSpan(offset + 48, 16).SequenceEqual("PAR 2.0\0FileDesc"u8))
            {
                conflicting[offset + 80] ^= 0xFF;
#pragma warning disable CA5351
                MD5.HashData(conflicting.AsSpan(offset + 32, length - 32)).CopyTo(conflicting.AsSpan(offset + 16, 16));
#pragma warning restore CA5351
                break;
            }
            offset += length;
        }
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length), [4]),
        ], [], DavItem.ItemSubType.MultipartFile, parity: (good.indexBytes.Concat(conflicting).ToArray(), good.volumeBytes));
        var id = release.Files[0].Ids[4];
        Assert.Equal(Par2RepairOutcome.NotRepaired, await release.Service.TryPar2RepairAsync(release.Item, [id], CancellationToken.None));
        Assert.False(release.Store.HasUsablePatch(id));
        Assert.Contains("Conflicting critical", (await ReadJobAsync()).FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admission_CanceledWaiterLeavesNoJobAndCanRetry()
    {
        var data = Data(4096 * 6, "admission");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length), [4]),
        ], [1], DavItem.ItemSubType.MultipartFile);
        var other = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, "other-mounted.mkv", release.Item.FileSize,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.MultipartFile, release.Item.ReleaseDate, null, null,
            release.Item.FileBlobId, release.Item.NzbBlobId);
        await using (var context = new DavDatabaseContext())
        {
            context.Items.Add(other);
            await context.SaveChangesAsync();
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release.Service.BeforePatchPublicationForTests = token => { entered.TrySetResult(); return proceed.Task.WaitAsync(token); };
        using var ownerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ids = new[] { release.Files[0].Ids[4] };
        var owner = release.Service.TryPar2RepairAsync(release.Item, ids, ownerCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(ownerCancellation.Token);
            using var waiterCancellation = new CancellationTokenSource();
            var waiter = release.Service.TryPar2RepairAsync(other, ids, waiterCancellation.Token);
            var sameItem = release.Service.TryPar2RepairAsync(release.Item, ids, ownerCancellation.Token);
            var snapshot = release.Service.GetDiagnosticSnapshot();
            Assert.Equal(1, snapshot.AdmissionActive);
            Assert.Equal(1, snapshot.AdmissionWaiters);
            Assert.False(sameItem.IsCompleted);
            await waiterCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
            Assert.Equal(0, release.Service.GetDiagnosticSnapshot().AdmissionWaiters);
            await using (var context = new DavDatabaseContext())
                Assert.False(await context.Par2RepairJobs.AnyAsync(job => job.DavItemId == other.Id));
            proceed.TrySetResult();
            Assert.Equal(Par2RepairOutcome.Repaired, await owner);
            Assert.Equal(Par2RepairOutcome.Repaired, await sameItem);
            Assert.Equal(Par2RepairOutcome.Repaired, await release.Service.TryPar2RepairAsync(other, ids, ownerCancellation.Token));
            Assert.Equal(0, release.Service.GetDiagnosticSnapshot().AdmissionActive);
        }
        finally
        {
            proceed.TrySetResult();
            await ownerCancellation.CancelAsync();
            try { await owner; }
            catch (OperationCanceledException) when (ownerCancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task LateDifferentVolumeRequest_RunsFollowUpBeforeReturningSuccess()
    {
        var first = Data(4096 * 6, "late-first");
        var second = Data(4096 * 7, "late-second");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("first.rar", first, Sizes(first.Length), [4]),
            new("second.rar", second, Sizes(second.Length)),
        ], [1, 2], DavItem.ItemSubType.MultipartFile);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publications = 0;
        release.Service.BeforePatchPublicationForTests = token =>
        {
            if (Interlocked.Increment(ref publications) != 1) return Task.CompletedTask;
            entered.TrySetResult();
            return proceed.Task.WaitAsync(token);
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var owner = release.Service.TryPar2RepairAsync(release.Item, [release.Files[0].Ids[4]], cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(cancellation.Token);
            var lateId = release.Files[1].Ids[5];
            var late = release.Service.TryPar2RepairAsync(release.Item, [lateId], cancellation.Token);
            Assert.False(late.IsCompleted);
            Assert.False(release.Store.HasUsablePatch(lateId));
            Assert.Equal(0, release.Service.GetDiagnosticSnapshot().AdmissionWaiters);
            proceed.TrySetResult();
            Assert.Equal(Par2RepairOutcome.Repaired, await owner);
            Assert.Equal(Par2RepairOutcome.Repaired, await late);
            await AssertPatchAsync(release, 1, 5);
            await using var context = new DavDatabaseContext();
            Assert.Equal(2, await context.Par2RepairJobs.CountAsync(job => job.State == Par2RepairJob.RepairJobState.Succeeded));
        }
        finally
        {
            proceed.TrySetResult();
            await cancellation.CancelAsync();
            try { await owner; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task CancellationBeforePublication_LeavesNoPatchOrRunningJob()
    {
        var data = Data(4096 * 6, "cancel-publication");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length), [4]),
        ], [1], DavItem.ItemSubType.MultipartFile);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        release.Service.BeforePatchPublicationForTests = async _ => await cancellation.CancelAsync();
        var id = release.Files[0].Ids[4];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => release.Service.TryPar2RepairAsync(release.Item, [id], cancellation.Token));
        Assert.False(release.Store.HasUsablePatch(id));
        var job = await ReadJobAsync();
        Assert.Equal(Par2RepairJob.RepairJobState.Failed, job.State);
        Assert.Null(job.NextAttemptAt);
        Assert.Equal(0, release.Service.GetDiagnosticSnapshot().AdmissionActive);
    }

    [Theory]
    [InlineData(DavItem.ItemSubType.MultipartFile)]
    [InlineData(DavItem.ItemSubType.RarFile)]
    public async Task PublicPlaybackReport_ReachesWorkerAndServesOfflineAfterRestart(DavItem.ItemSubType subtype)
    {
        var data = Data(4096 * 6, "public-trigger");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length), [4]),
        ], [1], subtype);
        var id = release.Files[0].Ids[4];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var workers = release.Service.RunWorkersAsync(cancellation.Token);
        try
        {
            release.Service.ReportZeroFill(release.Item.Path, id);
            release.Service.ReportZeroFill(release.Item.Path, id);
            await WaitForSuccessfulJobAsync(cancellation.Token);
            await AssertPatchAsync(release, 0, 4);

            var reloaded = new RepairPatchStore(release.PatchDirectory, release.Store.MaxBytes);
            await reloaded.EnsureCatalogLoadedAsync(cancellation.Token);
            using var offline = new FakeNntpClient(new Dictionary<string, byte[]>());
            using var repaired = new RepairedSegmentNntpClient(offline, reloaded);
            Assert.True((await repaired.StatAsync(id, cancellation.Token)).ArticleExists);
            var response = await repaired.DecodedBodyAsync(id, cancellation.Token);
            await using var stream = response.Stream!;
            await using var output = new MemoryStream();
            await stream.CopyToAsync(output, cancellation.Token);
            Assert.Equal(data.AsSpan(4096 * 4, 4096).ToArray(), output.ToArray());
            Assert.Equal(0, offline.BodyRequestCount);
            Assert.Empty(offline.StatRequestOrder);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await workers; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private static async Task WaitForSuccessfulJobAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (true)
        {
            await using var context = new DavDatabaseContext();
            var jobs = await context.Par2RepairJobs.AsNoTracking().ToListAsync(ct);
            Assert.DoesNotContain(jobs, job => job.State is Par2RepairJob.RepairJobState.Failed or Par2RepairJob.RepairJobState.Infeasible);
            if (jobs.Any(job => job.State == Par2RepairJob.RepairJobState.Succeeded)) return;
            await timer.WaitForNextTickAsync(ct);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ObfuscatedNonuniformVolumes_WithMissingPrefix_UseExactGeometry(bool trusted, bool pending)
    {
        var first = Data(28000, "nonuniform-first");
        var second = Data(31000, "nonuniform-second");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("protected.part1.rar", first, [777, 3223, 5000, 9000, 10000], [0], Subject: "first-obfuscated"),
            new("protected.part2.rar", second, [1000, 5000, 7000, 8000, 10000], [0], Subject: "second-obfuscated"),
        ], [1, 2, 3], DavItem.ItemSubType.MultipartFile, trusted, pending, obfuscatedParity: true);
        var ids = new[] { release.Files[0].Ids[0], release.Files[1].Ids[0] };

        Assert.Equal(Par2RepairOutcome.Repaired, await release.Service.TryPar2RepairAsync(release.Item, ids, CancellationToken.None));
        await AssertPatchAsync(release, 0, 0);
        await AssertPatchAsync(release, 1, 0);
        Assert.All(ids, id => Assert.False(release.Fake.BodyRequestCounts.ContainsKey(id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreportedSiblingDamage_ParticipatesInUnionAndCap(bool restrictCap)
    {
        if (restrictCap) _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RepairPar2MaxMissingSlices, ConfigValue = "1" }]);
        var first = Data(4096 * 7, "reported");
        var second = Data(4096 * 8, "unreported");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("first.rar", first, Sizes(first.Length), [4]),
            new("second.rar", second, Sizes(second.Length), [6]),
        ], [1, 2], DavItem.ItemSubType.MultipartFile);
        var result = await release.Service.TryPar2RepairAsync(release.Item, [release.Files[0].Ids[4]], CancellationToken.None);
        Assert.Equal(restrictCap ? Par2RepairOutcome.NotRepaired : Par2RepairOutcome.Repaired, result);
        if (restrictCap)
        {
            Assert.False(release.Store.HasUsablePatch(release.Files[0].Ids[4]));
            Assert.Contains("exceeds cap", (await ReadJobAsync()).FailureReason, StringComparison.Ordinal);
        }
        else
        {
            await AssertPatchAsync(release, 0, 4);
            await AssertPatchAsync(release, 1, 6);
            Assert.Equal(2, (await ReadJobAsync()).SlicesReconstructed);
        }
    }

    [Fact]
    public async Task FinalVolumeHashFailure_PublishesNeitherVolume()
    {
        var first = Data(4096 * 6, "valid-volume");
        var second = Data(4096 * 7, "invalid-volume-hash");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("first.rar", first, Sizes(first.Length), [4]),
            new("second.rar", second, Sizes(second.Length), [5], FileHashOverride: new byte[16]),
        ], [1, 2], DavItem.ItemSubType.MultipartFile);
        var ids = new[] { release.Files[0].Ids[4], release.Files[1].Ids[5] };
        Assert.Equal(Par2RepairOutcome.NotRepaired, await release.Service.TryPar2RepairAsync(release.Item, ids, CancellationToken.None));
        Assert.All(ids, id => Assert.False(release.Store.HasUsablePatch(id)));
        var job = await ReadJobAsync();
        Assert.Equal(Par2RepairJob.RepairJobState.Failed, job.State);
        Assert.Contains("Whole-file MD5", job.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipartVerifyAll_IsExplicitlyRejected()
    {
        var data = Data(4096 * 3, "verify-all");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length)),
        ], [1], DavItem.ItemSubType.MultipartFile);
        Assert.Equal(Par2RepairOutcome.NotRepaired, await release.Service.TryPar2RepairAsync(release.Item, null, CancellationToken.None));
        Assert.Equal("PAR2 full verification supports plain NZB files only; multi-volume repair requires specific segment ids.",
            (await ReadJobAsync()).FailureReason);
        Assert.Equal(0, release.Fake.BodyRequestCount);
    }

    [Fact]
    public async Task UntrustedAdjacentMissingArticles_AreNotGuessed()
    {
        var data = Data(4096 * 7, "ambiguous-ranges");
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length), [4, 5]),
        ], [1, 2], DavItem.ItemSubType.MultipartFile, trustedRanges: false);
        var ids = new[] { release.Files[0].Ids[4], release.Files[0].Ids[5] };
        Assert.Equal(Par2RepairOutcome.NotRepaired, await release.Service.TryPar2RepairAsync(release.Item, ids, CancellationToken.None));
        Assert.All(ids, id => Assert.False(release.Store.HasUsablePatch(id)));
        Assert.Contains("ambiguous", (await ReadJobAsync()).FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongSameSizeSourceWithAvailableParity_CannotProveIdentity()
    {
        var data = Data(4096 * 4, "actual");
        var wrong = Data(data.Length, "wrong");
        var parity = Par2TestEncoder.EncodeSet([("volume.rar", wrong)], 4096, [1u, 2u, 3u, 4u]);
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("volume.rar", data, Sizes(data.Length), [0]),
        ], [], DavItem.ItemSubType.MultipartFile, parity: parity);
        var id = release.Files[0].Ids[0];
        Assert.Equal(Par2RepairOutcome.NotRepaired, await release.Service.TryPar2RepairAsync(release.Item, [id], CancellationToken.None));
        Assert.False(release.Store.HasUsablePatch(id));
    }

    [Theory]
    [InlineData("single", 4096)]
    [InlineData("uneven", 6144)]
    public async Task RealCorpus_PlainRepairTrimsFinalPatch(string prefix, int sliceSize)
    {
        var data = await File.ReadAllBytesAsync(Path.Combine(Par2CmdlineInteropTests.CorpusDirectory, "alpha.bin"));
        var sizes = Enumerable.Range(0, (data.Length + sliceSize - 1) / sliceSize)
            .Select(index => Math.Min(sliceSize, data.Length - sliceSize * index)).ToArray();
        var parity = await CorpusParityAsync(prefix);
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync([
            new("alpha.bin", data, sizes, [sizes.Length - 1]),
        ], [], parity: parity);
        var id = release.Files[0].Ids[^1];
        Assert.Equal(Par2RepairOutcome.Repaired, await release.Service.TryPar2RepairAsync(release.Item, [id], CancellationToken.None));
        await AssertPatchAsync(release, 0, sizes.Length - 1);
    }

    [Fact]
    public async Task RealCorpus_MultipartRepairsTwoVolumesIncludingThreeByteTail()
    {
        var files = new List<Par2RepairTestReleaseBuilder.SourceFile>();
        foreach (var name in new[] { "alpha.bin", "beta.bin", "gamma.bin" })
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Par2CmdlineInteropTests.CorpusDirectory, name));
            files.Add(new(name, bytes, Sizes(bytes.Length), name == "alpha.bin" ? [0] : name == "beta.bin" ? [6] : []));
        }
        await using var release = await new Par2RepairTestReleaseBuilder(_config, _root).BuildAsync(files, [],
            DavItem.ItemSubType.MultipartFile, parity: await CorpusParityAsync("set"));
        Assert.Equal(Par2RepairOutcome.Repaired, await release.Service.TryPar2RepairAsync(release.Item,
            [release.Files[0].Ids[0], release.Files[1].Ids[6]], CancellationToken.None));
        await AssertPatchAsync(release, 0, 0);
        await AssertPatchAsync(release, 1, 6);
        Assert.Equal(2, (await ReadJobAsync()).SlicesReconstructed);
    }

    private static async Task<(byte[] Index, byte[] Recovery)> CorpusParityAsync(string prefix)
        => (await File.ReadAllBytesAsync(Path.Combine(Par2CmdlineInteropTests.CorpusDirectory, prefix + ".par2")),
            await File.ReadAllBytesAsync(Directory.GetFiles(Par2CmdlineInteropTests.CorpusDirectory, prefix + ".vol*.par2").Single()));

    private static async Task AssertPatchAsync(Par2RepairTestReleaseBuilder.SeededRelease release, int fileIndex, int segmentIndex)
    {
        var file = release.Files[fileIndex];
        var range = file.Ranges[segmentIndex];
        Assert.True(release.Store.TryGet(file.Ids[segmentIndex], out var response));
        await using var stream = response!.Stream!;
        var header = await stream.GetYencHeadersAsync();
        Assert.NotNull(header);
        Assert.Equal(file.Source.Data.Length, header.FileSize);
        Assert.Equal(segmentIndex + 1, header.PartNumber);
        Assert.Equal(file.Ids.Length, header.TotalParts);
        Assert.Equal(range.StartInclusive, header.PartOffset);
        Assert.Equal(range.Count, header.PartSize);
        await using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        Assert.Equal(file.Source.Data.AsSpan((int)range.StartInclusive, (int)range.Count).ToArray(), output.ToArray());
    }

    private static async Task<Par2RepairJob> ReadJobAsync()
    {
        await using var context = new DavDatabaseContext();
        return await context.Par2RepairJobs.SingleAsync();
    }

    [Fact]
    public void VolumeGeometry_CompletesOnlyUnambiguousMissingRanges()
    {
        var ranges = Par2RepairService.CompleteVolumeRanges(12000,
            [null, LongRange.FromStartAndSize(2000, 3000), null, LongRange.FromStartAndSize(11000, 1000)]);
        Assert.Equal(new long[] { 2000, 3000, 6000, 1000 }, ranges.Select(range => range.Count));
        Assert.Equal(12000, ranges[^1].EndExclusive);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void VolumeGeometry_RejectsAmbiguousOrConflictingCoverage(int scenario)
    {
        LongRange?[] ranges = scenario switch
        {
            0 => [null, null, LongRange.FromStartAndSize(8000, 4000)],
            1 => [LongRange.FromStartAndSize(0, 5000), LongRange.FromStartAndSize(4000, 8000)],
            2 => [LongRange.FromStartAndSize(0, 4000), LongRange.FromStartAndSize(5000, 7000)],
            _ => [LongRange.FromStartAndSize(0, 4000), LongRange.FromStartAndSize(4000, 7000)],
        };
        Assert.Throws<InvalidDataException>(() => Par2RepairService.CompleteVolumeRanges(12000, ranges));
    }

    private static byte[] Data(int length, string seed)
    {
        var data = new byte[length];
        for (var offset = 0; offset < length; offset += 32)
        {
            var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes($"{seed}:{offset}"));
            hash.AsSpan(0, Math.Min(32, length - offset)).CopyTo(data.AsSpan(offset));
        }
        return data;
    }
}