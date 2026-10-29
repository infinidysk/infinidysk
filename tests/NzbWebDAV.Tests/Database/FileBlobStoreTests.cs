using MemoryPack;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using ZstdSharp;

namespace NzbWebDAV.Tests.Database;

[Collection(nameof(ConfigPathCollection))]
public sealed class FileBlobStoreTests : IDisposable
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-file-blob-store-{Guid.NewGuid():N}");
    private readonly string? _previousConfigPath;
    private readonly FileBlobStore _store = new();

    public FileBlobStoreTests()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);
    }

    public void Dispose()
    {
        _store.Dispose();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string BlobPath(Guid id)
    {
        var hex = id.ToString("N");
        return Path.Join(_configRoot, "blobs", hex[..2], hex.Substring(2, 2), id.ToString());
    }

    // Raw WriteBlob(Stream) compresses, so on-disk corruption is planted directly.
    private void PlantBlobFile(Guid id, byte[] bytes)
    {
        var path = BlobPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static async Task<byte[]> ReadAllAsync(Stream? stream)
    {
        Assert.NotNull(stream);
        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream!.CopyToAsync(buffer);
            return buffer.ToArray();
        }
    }

    [Fact]
    public async Task WriteBlob_StoresStreamZstdCompressed_AndReadReturnsOriginalBytes()
    {
        var id = Guid.NewGuid();
        var nzb = System.Text.Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><nzb><file><segments>" +
            string.Concat(Enumerable.Range(0, 200).Select(i =>
                $"<segment bytes=\"768000\" number=\"{i + 1}\">part{i}of200.abcdef@example.com</segment>")) +
            "</segments></file></nzb>");

        await _store.WriteBlob(id, new MemoryStream(nzb));

        var onDisk = await File.ReadAllBytesAsync(BlobPath(id));
        Assert.Equal(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, onDisk[..4]);
        Assert.True(onDisk.Length < nzb.Length / 2, $"expected compression, got {onDisk.Length} of {nzb.Length} bytes");
        Assert.Equal(nzb, await ReadAllAsync(_store.ReadBlob(id)));
    }

    [Fact]
    public async Task ReadBlob_ReturnsLegacyUncompressedBlobUnchanged()
    {
        var id = Guid.NewGuid();
        var legacy = "<?xml version=\"1.0\"?><nzb />"u8.ToArray();
        PlantBlobFile(id, legacy);

        Assert.Equal(legacy, await ReadAllAsync(_store.ReadBlob(id)));
    }

    [Fact]
    public async Task ReadBlob_DecompressesZstdBlob_IntoSeekableStream()
    {
        var id = Guid.NewGuid();
        var original = "<?xml version=\"1.0\"?><nzb><file subject=\"zstd\" /></nzb>"u8.ToArray();
        using (var compressor = new Compressor())
            PlantBlobFile(id, compressor.Wrap(original).ToArray());

        await using var stream = _store.ReadBlob(id);
        Assert.NotNull(stream);
        Assert.True(stream!.CanSeek);
        Assert.Equal(original.Length, stream.Length);
        Assert.Equal(original, await ReadAllAsync(stream));
    }

    [Fact]
    public async Task ReadBlob_ShorterThanZstdMagic_IsReturnedUnchanged()
    {
        var id = Guid.NewGuid();
        byte[] tiny = [0x28, 0xB5];
        PlantBlobFile(id, tiny);

        Assert.Equal(tiny, await ReadAllAsync(_store.ReadBlob(id)));
    }

    [Fact]
    public async Task WriteReadDelete_RoundTripsStreamBytes()
    {
        var id = Guid.NewGuid();
        var payload = "blob-store-roundtrip"u8.ToArray();
        await using (var input = new MemoryStream(payload))
            await _store.WriteBlob(id, input);
        Assert.True(_store.Exists(id));

        await using (var output = _store.ReadBlob(id))
        {
            Assert.NotNull(output);
            using var buffer = new MemoryStream();
            await output!.CopyToAsync(buffer);
            Assert.Equal(payload, buffer.ToArray());
        }

        Assert.True(_store.Delete(id));
        Assert.False(_store.Exists(id));
        Assert.False(_store.Delete(id));
        Assert.Null(_store.ReadBlob(id));
    }

    [Fact]
    public async Task WriteBlob_HonorsCancellation_DoesNotLeavePartialFile()
    {
        var id = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await using var input = new MemoryStream(new byte[1024]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.WriteBlob(id, input, cts.Token));

        Assert.Null(_store.ReadBlob(id));
        var blobsRoot = Path.Join(_configRoot, "blobs");
        if (Directory.Exists(blobsRoot))
        {
            Assert.Empty(Directory.EnumerateFiles(blobsRoot, "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task ConcurrentDelete_ReportsOnlyOnePhysicalRemoval()
    {
        var id = Guid.NewGuid();
        await _store.WriteBlob(id, new MemoryStream("concurrent-delete"u8.ToArray()));

        var results = await Task.WhenAll(
            Task.Run(() => _store.Delete(id)),
            Task.Run(() => _store.Delete(id)));

        Assert.Equal(1, results.Count(result => result));
        Assert.Null(_store.ReadBlob(id));
    }

    [Fact]
    public async Task ReadBlobOfT_ThrowsCorrupted_WhenBlobFileIsEmpty()
    {
        var id = Guid.NewGuid();
        PlantBlobFile(id, []);

        var ex = await Assert.ThrowsAsync<CorruptedBlobPayloadException>(
            () => _store.ReadBlob<DavNzbFile>(id));

        Assert.Equal(id, ex.BlobId);
        Assert.Equal(typeof(DavNzbFile), ex.PayloadType);
        Assert.IsType<MemoryPackSerializationException>(ex.InnerException);
    }

    [Fact]
    public async Task ReadBlobOfT_ThrowsCorrupted_WhenDataIsNotValidZstd()
    {
        var id = Guid.NewGuid();
        PlantBlobFile(id, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);

        var ex = await Assert.ThrowsAsync<CorruptedBlobPayloadException>(
            () => _store.ReadBlob<DavNzbFile>(id));

        Assert.Equal(id, ex.BlobId);
        Assert.IsType<ZstdException>(ex.InnerException);
    }

    [Fact]
    public async Task ReadBlobOfT_ThrowsCorrupted_WhenZstdFrameIsTruncated()
    {
        var id = Guid.NewGuid();
        var original = new DavNzbFile
        {
            Id = id,
            SegmentIds = Enumerable.Range(0, 500).Select(i => $"<segment-{i}@example>").ToArray(),
        };

        using var compressed = new MemoryStream();
        await using (var compressionStream = new CompressionStream(compressed, leaveOpen: true))
            await MemoryPackSerializer.SerializeAsync(compressionStream, original);
        var fullBytes = compressed.ToArray();
        var truncated = fullBytes[..(fullBytes.Length / 2)];
        PlantBlobFile(id, truncated);

        var ex = await Assert.ThrowsAsync<CorruptedBlobPayloadException>(
            () => _store.ReadBlob<DavNzbFile>(id));

        Assert.Equal(id, ex.BlobId);
        Assert.IsType<EndOfStreamException>(ex.InnerException);
    }

    [Fact]
    public async Task ReadBlobOfT_DoesNotCache_AfterCorruptedRead()
    {
        var id = Guid.NewGuid();
        PlantBlobFile(id, []);

        await Assert.ThrowsAsync<CorruptedBlobPayloadException>(() => _store.ReadBlob<DavNzbFile>(id));

        // A subsequent write with valid data must not be shadowed by a cached failure.
        var replacement = new DavNzbFile { Id = id, SegmentIds = ["<ok@example>"] };
        await _store.WriteBlob(id, replacement);
        var recovered = await _store.ReadBlob<DavNzbFile>(id);

        Assert.NotNull(recovered);
        Assert.Equal(replacement.SegmentIds, recovered!.SegmentIds);
    }
}
