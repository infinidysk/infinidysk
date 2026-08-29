using NzbWebDAV.Database;

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
}
