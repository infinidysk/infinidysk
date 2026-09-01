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
        await _store.WriteBlob(id, new MemoryStream());

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
        await _store.WriteBlob(id, new MemoryStream([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]));

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
        await _store.WriteBlob(id, new MemoryStream(truncated));

        var ex = await Assert.ThrowsAsync<CorruptedBlobPayloadException>(
            () => _store.ReadBlob<DavNzbFile>(id));

        Assert.Equal(id, ex.BlobId);
        Assert.IsType<EndOfStreamException>(ex.InnerException);
    }

    [Fact]
    public async Task ReadBlobOfT_DoesNotCache_AfterCorruptedRead()
    {
        var id = Guid.NewGuid();
        await _store.WriteBlob(id, new MemoryStream());

        await Assert.ThrowsAsync<CorruptedBlobPayloadException>(() => _store.ReadBlob<DavNzbFile>(id));

        // A subsequent write with valid data must not be shadowed by a cached failure.
        var replacement = new DavNzbFile { Id = id, SegmentIds = ["<ok@example>"] };
        await _store.WriteBlob(id, replacement);
        var recovered = await _store.ReadBlob<DavNzbFile>(id);

        Assert.NotNull(recovered);
        Assert.Equal(replacement.SegmentIds, recovered!.SegmentIds);
    }
}
