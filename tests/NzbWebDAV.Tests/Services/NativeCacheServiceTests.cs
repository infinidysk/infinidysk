using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class NativeCacheServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-service-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("CONFIG_PATH");

    public NativeCacheServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        Environment.SetEnvironmentVariable("CONFIG_PATH", _root);
    }

    [Fact]
    public async Task NativeBufferBudget_IsIncludedInMemoryOwnershipSnapshot()
    {
        using var blobs = new FileBlobStore();
        using var repairs = new RepairPatchStore(Path.Combine(_root, "patches"), 100);
        var config = Config("native");
        await using var native = new NativeCacheService(config, blobs, repairs);
        var builder = new NzbWebDAV.Services.Diagnostics.MemoryComponentSnapshotBuilder(
            new InFlightArticleBudget(1000), config,
            new NzbWebDAV.Services.ConcurrentReadTracker(configManager: config), new NzbWebDAV.Services.ActiveReadRegistry(),
            new NzbWebDAV.Clients.Usenet.SegmentCacheStatistics(), native);
        try
        {
            var snapshot = builder.Capture();
            Assert.Equal(native.ReservedBufferBytes, snapshot.NativeCache!.ReservedBufferBytes);
            Assert.Equal(native.ActiveSettings!.BufferMb * 1024L * 1024, snapshot.NativeCache.BufferBudgetBytes);
        }
        finally
        {
            // Snapshot capture intentionally does not wait for storage startup.
            // Bounded host disposal may leave initialization running; the test owns
            // its temporary directory until that background work has really ended.
            await native.DisposeAsync();
            await native.InitializationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task InvalidNativeSettings_StatusStillReportsInitializationFailure()
    {
        using var blobs = new FileBlobStore();
        using var repairs = new RepairPatchStore(Path.Combine(_root, "patches"), 100);
        var config = Config("native");
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.NativeCacheFolders, ConfigValue = "not-json" }]);
        await using var native = new NativeCacheService(config, blobs, repairs);
        Assert.NotNull(native.InitializationError);
        Assert.True(native.RequiresRestart(config));
    }

    [Fact]
    public async Task StalledNativeInitialization_DoesNotBlockServiceConstruction()
    {
        using var blobs = new FileBlobStore();
        using var repairs = new RepairPatchStore(Path.Combine(_root, "patches"), 100);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = Task.Run(() => new NativeCacheService(Config("native"), blobs, repairs, settings =>
        {
            entered.TrySetResult();
            release.Wait();
            return new NativeCacheStore(Path.Combine(settings.MetadataPath, "catalogue.db"), settings.Folders);
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var native = await created.WaitAsync(TimeSpan.FromSeconds(1));
            native.InitializationWait = TimeSpan.FromMilliseconds(50);
            Assert.True(native.InitializationPending);
            await using var stream = await native.WrapAsync(new DavItem(), _ => Task.FromResult<Stream>(new VerifiedStream()), CancellationToken.None);
            Assert.Equal(3, await stream.ReadAsync(new byte[3]));
            await native.DisposeAsync();
        }
        finally
        {
            release.Set();
            var native = await created;
            await native.DisposeAsync();
            await native.InitializationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(101, 50)]
    [InlineData(90, 0)]
    public void NativeFolders_RejectInvalidEvictionWatermarks(int high, int low)
    {
        var json = JsonSerializer.Serialize(new[] { new { Id = "disk", Path = "/cache",
            HighWaterPercent = high, LowWaterPercent = low } });
        Assert.Throws<ArgumentException>(() => NativeCacheSettings.ParseFolders(json));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("segment")]
    [InlineData("native")]
    public void RepairRevisionPath_DoesNotParseUnrelatedInvalidNativeSettings(string mode)
    {
        var config = Config(mode);
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.NativeCacheFolders, ConfigValue = "invalid-json" },
            new ConfigItem { ConfigName = ConfigKeys.NativeCacheWriterMb, ConfigValue = "invalid-budget" }
        ]);
        Assert.Equal(Path.Combine(_root, "index", "repair-revisions.db"), NativeCacheSettings.RepairRevisionPath(config));
        Assert.False(Directory.Exists(Path.Combine(_root, "index")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopenedNativeCache_HitDoesNotOpenUnderlyingMedia(bool unrelatedRepair)
    {
        using var blobs = new FileBlobStore();
        var blobId = Guid.NewGuid();
        await blobs.WriteBlob(blobId, new DavNzbFile { Id = blobId, SegmentIds = ["movie-segment"] });
        var item = new DavItem { Id = Guid.NewGuid(), Name = "movie.mkv", FileSize = 3, FileBlobId = blobId, SubType = DavItem.ItemSubType.NzbFile };
        using var repair = new RepairPatchStore(Path.Combine(_root, "patches"), 100);
        await using (var service = new NativeCacheService(Config("native"), blobs, repair))
        await using (var stream = await service.WrapAsync(item, _ => Task.FromResult<Stream>(new VerifiedStream()), CancellationToken.None))
        {
            Assert.IsType<NativeCachedStream>(stream);
            Assert.Equal(3, await stream.ReadAsync(new byte[3]));
        }
        if (unrelatedRepair) repair.CommitPatch("another-movie", [1, 2, 3], new UsenetSharp.Models.UsenetYencHeader
        {
            FileName = "another.mkv", FileSize = 3, PartSize = 3, PartOffset = 0, PartNumber = 1, TotalParts = 1, LineLength = 128
        });
        await using var reopened = new NativeCacheService(Config("native"), blobs, repair);
        await using var hit = await reopened.WrapAsync(item, _ => throw new InvalidOperationException("Opened media on cache hit"), CancellationToken.None);
        Assert.Equal(3, await hit.ReadAsync(new byte[3]));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("segment")]
    public async Task OtherModes_DoNotInitializeNativeStorage(string mode)
    {
        using var blobs = new FileBlobStore();
        using var repair = new RepairPatchStore(Path.Combine(_root, "patches"), 100);
        await using var service = new NativeCacheService(Config(mode), blobs, repair);
        Assert.Null(service.Store);
        Assert.False(Directory.Exists(Path.Combine(_root, "index")));
    }

    [Fact]
    public async Task RequiredNativeAdmission_DoesNotOpenRawSourceWhenDisabled()
    {
        using var blobs = new FileBlobStore();
        using var repair = new RepairPatchStore(Path.Combine(_root, "patches"), 100);
        await using var service = new NativeCacheService(Config("off"), blobs, repair);
        var opened = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WrapAsync(new DavItem(), _ =>
        {
            opened = true;
            return Task.FromResult<Stream>(new MemoryStream());
        }, CancellationToken.None, requireNative: true));
        Assert.False(opened);
    }

    [Fact]
    public async Task MissingMetadata_SourceFailureIsNotRetriedByCache()
    {
        using var blobs = new FileBlobStore();
        using var repair = new RepairPatchStore(Path.Combine(_root, "patches"), 100);
        await using var service = new NativeCacheService(Config("native"), blobs, repair);
        var item = new DavItem { Id = Guid.NewGuid(), Name = "missing.mkv", FileBlobId = Guid.NewGuid(), FileSize = 3 };
        var opens = 0;
        await Assert.ThrowsAsync<IOException>(() => service.WrapAsync(item, _ =>
        {
            opens++;
            throw new IOException("source unavailable");
        }, CancellationToken.None));
        Assert.Equal(1, opens);
        Assert.Equal(0, service.ReservedBufferBytes);
    }

    private ConfigManager Config(string mode)
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = mode },
            new ConfigItem { ConfigName = ConfigKeys.NativeCacheMetadataPath, ConfigValue = Path.Combine(_root, "index") },
            new ConfigItem { ConfigName = ConfigKeys.NativeCacheFolders, ConfigValue = JsonSerializer.Serialize(new[] {
                new NativeCacheFolder { Id = "media", Path = Path.Combine(_root, "media"), MinFreeBytes = 0 }
            }) }
        ]);
        return config;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _oldConfig);
        Directory.Delete(_root, recursive: true);
    }

    private sealed class VerifiedStream() : MemoryStream(new byte[] { 1, 2, 3 }), ICacheReadEvidence
    {
        public bool LastReadCacheable => true;
    }
}
