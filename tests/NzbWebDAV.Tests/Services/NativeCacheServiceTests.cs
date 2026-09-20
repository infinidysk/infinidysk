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
