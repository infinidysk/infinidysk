using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.Fakes;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class RepairedSegmentNntpClientTests
{
    private static UsenetYencHeader HeaderFor(byte[] content) => new()
    {
        FileName = "test.bin",
        FileSize = content.Length,
        LineLength = 128,
        PartNumber = 1,
        TotalParts = 1,
        PartOffset = 0,
        PartSize = content.Length,
    };

    [Fact]
    public async Task PatchedSegment_ServedWithoutProviderBody_AndFiresRetrieved()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-patch-" + Guid.NewGuid().ToString("N"));
        const string segmentId = "missing-article@test";
        byte[] content = "repaired-bytes"u8.ToArray();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.CatalogLoadTask;
            store.CommitPatch(segmentId, content, HeaderFor(content));

            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);

            ArticleBodyResult? completion = null;
            var response = await client.DecodedBodyAsync(
                segmentId,
                (result, _) => completion = result,
                CancellationToken.None);

            Assert.Equal(0, inner.BodyRequestCount);
            Assert.Equal(ArticleBodyResult.Retrieved, completion);
            Assert.NotNull(response.Stream);
            await using var output = new MemoryStream();
            await response.Stream.CopyToAsync(output);
            Assert.Equal(content, output.ToArray());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task UnpatchedSegment_FallsThroughToInner()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-patch-" + Guid.NewGuid().ToString("N"));
        const string segmentId = "live@test";
        byte[] content = "provider-bytes"u8.ToArray();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.CatalogLoadTask;
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>
            {
                [segmentId] = content,
            }, useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            Assert.Equal(1, inner.BodyRequestCount);
            await using var output = new MemoryStream();
            await response.Stream!.CopyToAsync(output);
            Assert.Equal(content, output.ToArray());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
