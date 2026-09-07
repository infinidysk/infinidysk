using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.Database;
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