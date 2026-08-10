using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Tests.Fakes;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class SegmentCacheNntpClientTests
{
    [Fact]
    public async Task CatalogHydration_DoesNotBlockConstruction_AndServesEntriesAfterLoad()
    {
        var cacheDir = Path.Join(
            Path.GetTempPath(), "nzbdav-segment-cache-" + Guid.NewGuid().ToString("N"));
        var loadStarted = new ManualResetEventSlim();
        var allowLoad = new ManualResetEventSlim();
        const string segmentId = "segment-1";
        byte[] content = "cached-content"u8.ToArray();

        try
        {
            WriteCacheEntry(cacheDir, segmentId, content);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>
            {
                [segmentId] = content,
            }, useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024 * 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: () =>
                {
                    loadStarted.Set();
                    allowLoad.Wait();
                    return Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories);
                });

            Assert.True(loadStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(client.IsCatalogReady);

            var beforeHydration = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            beforeHydration.Stream?.Dispose();
            Assert.Equal(1, inner.BodyRequestCount);

            allowLoad.Set();
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(client.IsCatalogReady);

            var afterHydration = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.Equal(1, inner.BodyRequestCount);
            Assert.NotNull(afterHydration.Stream);

            await using var output = new MemoryStream();
            await afterHydration.Stream.CopyToAsync(output);
            Assert.Equal(content, output.ToArray());
        }
        finally
        {
            allowLoad.Set();
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
            loadStarted.Dispose();
            allowLoad.Dispose();
        }
    }

    [Fact]
    public async Task CatalogHydration_DoesNotDoubleCountEntryFinalizedDuringLoad()
    {
        var cacheDir = Path.Join(
            Path.GetTempPath(), "nzbdav-segment-cache-" + Guid.NewGuid().ToString("N"));
        var loadStarted = new ManualResetEventSlim();
        var allowLoad = new ManualResetEventSlim();
        const string segmentId = "segment-written-during-load";
        byte[] content = "new-cache-content"u8.ToArray();

        try
        {
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>
            {
                [segmentId] = content,
            }, useCachedYencStreams: true);
            using var client = new SegmentCacheNntpClient(
                inner,
                cacheDir,
                maxBytes: 1024 * 1024,
                usageTracker: null,
                metricsWriter: null,
                enumerateCacheFiles: () =>
                {
                    loadStarted.Set();
                    allowLoad.Wait();
                    return Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories);
                });

            Assert.True(loadStarted.Wait(TimeSpan.FromSeconds(5)));
            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.NotNull(response.Stream);
            await response.Stream.CopyToAsync(Stream.Null);
            response.Stream.Dispose();
            Assert.Equal(content.Length, client.CurrentBytes);

            allowLoad.Set();
            await client.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(content.Length, client.CurrentBytes);
        }
        finally
        {
            allowLoad.Set();
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
            loadStarted.Dispose();
            allowLoad.Dispose();
        }
    }

    private static void WriteCacheEntry(string cacheDir, string segmentId, byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(segmentId)));
        var directory = Path.Join(cacheDir, hash[..2]);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Join(directory, hash), content);

        var header = new UsenetYencHeader
        {
            FileName = "fake.bin",
            FileSize = content.Length,
            LineLength = 128,
            PartNumber = 1,
            PartOffset = 0,
            PartSize = content.Length,
            TotalParts = 1,
        };
        File.WriteAllText(
            Path.Join(directory, hash) + ".h",
            JsonSerializer.Serialize(header, new JsonSerializerOptions { IncludeFields = true }));
    }
}
