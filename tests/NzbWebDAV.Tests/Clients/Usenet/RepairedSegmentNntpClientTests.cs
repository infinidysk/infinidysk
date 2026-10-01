using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class RepairedSegmentNntpClientTests
{
    [Fact]
    public async Task OmittedSegment_IsLocalMissingUntilPatched()
    {
        var dir = Path.Join(Path.GetTempPath(), "omitted-patch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            var marker = NzbFile.CreateOmittedSegmentId("first@example", 2);
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);
            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() => client.DecodedBodyAsync(marker, CancellationToken.None));
            Assert.False((await client.StatAsync(marker, CancellationToken.None)).ArticleExists);
            Assert.Null(await client.TryGetLocalDecodedBodyAsync(marker, CancellationToken.None));
            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() => client.GetYencHeadersAsync(marker, CancellationToken.None));
            Assert.Equal(0, store.HitCount);

            byte[] bytes = [4, 5, 6];
            var header = HeaderFor(bytes) with { FileSize = 9, PartNumber = 2, TotalParts = 3, PartOffset = 3 };
            store.CommitPatch(marker, bytes, header);
            Assert.True((await client.StatAsync(marker, CancellationToken.None)).ArticleExists);
            Assert.Equal(header, await client.GetYencHeadersAsync(marker, CancellationToken.None));
            var body = await client.DecodedBodyAsync(marker, CancellationToken.None);
            Assert.Equal(bytes, await ReadOmittedResponseAsync(body));
            var local = await client.TryGetLocalDecodedBodyAsync(marker, CancellationToken.None);
            Assert.NotNull(local);
            Assert.Equal(bytes, await ReadOmittedResponseAsync(local));
            Assert.Empty(inner.RequestedSegmentIds);
            Assert.Empty(inner.StatRequestOrder);
            Assert.Equal(0, inner.HeaderProbeCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task OmittedSegment_CompletionFiresExactlyOnce(bool exclusive, bool throwing, bool cancelled)
    {
        var dir = Path.Join(Path.GetTempPath(), "omitted-callback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            var marker = NzbFile.CreateOmittedSegmentId("first@example", 2);
            if (cancelled) store.CommitPatch(marker, [1], HeaderFor([1]));
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>());
            using var client = new RepairedSegmentNntpClient(inner, store);
            using var cancellation = new CancellationTokenSource();
            if (cancelled) cancellation.Cancel();
            var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: throwing);
            Task<UsenetDecodedBodyResponse> ReadAsync() => exclusive
                ? client.DecodedBodyAsync(marker, new UsenetExclusiveConnection(recorder.Invoke), cancellation.Token)
                : client.DecodedBodyAsync(marker, recorder.Invoke, cancellation.Token);
            if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(ReadAsync);
            else await Assert.ThrowsAsync<UsenetArticleNotFoundException>(ReadAsync);
            Assert.Equal(1, recorder.Count);
            Assert.Equal(cancelled ? ArticleBodyResult.Cancelled : ArticleBodyResult.NotFound, recorder.Result);
            Assert.Empty(inner.RequestedSegmentIds);
            Assert.Equal(0, store.HitCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OmittedSegment_BatchPreservesNeighboursAndOrder(bool exclusive)
    {
        var dir = Path.Join(Path.GetTempPath(), "omitted-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            var marker = NzbFile.CreateOmittedSegmentId("first@example", 2);
            var patched = NzbFile.CreateOmittedSegmentId("first@example", 4);
            store.CommitPatch(patched, [4], HeaderFor([4]));
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>
            {
                ["first@example"] = [1], ["third@example"] = [3],
            }, useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
            SegmentId[] ids = ["first@example", marker, "third@example", patched];
            var batch = exclusive
                ? await client.DecodedBodiesAsync(ids, new UsenetExclusiveConnection(recorder.Invoke), cancellation.Token)
                : await client.DecodedBodiesAsync(ids, recorder.Invoke, cancellation.Token);
            Assert.Equal(4, batch.Responses.Count);
            Assert.Equal(new byte[] { 1 }, await ReadOmittedResponseAsync(await batch.Responses[0].WaitAsync(cancellation.Token)));
            var missing = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() => batch.Responses[1].WaitAsync(cancellation.Token));
            Assert.Equal(marker, missing.SegmentId);
            Assert.Equal(new byte[] { 3 }, await ReadOmittedResponseAsync(await batch.Responses[2].WaitAsync(cancellation.Token)));
            Assert.Equal(new byte[] { 4 }, await ReadOmittedResponseAsync(await batch.Responses[3].WaitAsync(cancellation.Token)));
            await batch.Completion.WaitAsync(cancellation.Token);
            Assert.All(batch.Responses, response => Assert.True(response.IsCompleted));
            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.NotFound, recorder.Result);
            Assert.Equal(["first@example", "third@example"], inner.RequestedSegmentIds.Order(StringComparer.Ordinal));
            Assert.Equal(1, inner.BatchRequestCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task OmittedSegment_AllLocalBatchNeedsNoProvider(bool exclusive, bool withPatch, bool cancelled)
    {
        var dir = Path.Join(Path.GetTempPath(), "omitted-local-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            var marker = NzbFile.CreateOmittedSegmentId("first@example", 2);
            store.CommitPatch("patched@example", [1, 2, 3], HeaderFor([1, 2, 3]));
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>());
            using var client = new RepairedSegmentNntpClient(inner, store);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (cancelled) cancellation.Cancel();
            var recorder = new ArticleBodyCompletionRecorder();
            SegmentId[] ids = withPatch ? [marker, "patched@example", marker] : [marker, marker];
            Task<UsenetDecodedBodyBatch> FetchAsync() => exclusive
                ? client.DecodedBodiesAsync(ids, new UsenetExclusiveConnection(recorder.Invoke), cancellation.Token)
                : client.DecodedBodiesAsync(ids, recorder.Invoke, cancellation.Token);
            if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(FetchAsync);
            else
            {
                var batch = await FetchAsync();
                for (var index = 0; index < ids.Length; index++)
                {
                    if (NzbFile.IsOmittedSegmentId(ids[index]))
                        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() => batch.Responses[index].WaitAsync(cancellation.Token));
                    else
                    {
                        var response = await batch.Responses[index].WaitAsync(cancellation.Token);
                        await response.Stream!.DisposeAsync();
                    }
                }
                await batch.Completion.WaitAsync(cancellation.Token);
                Assert.All(batch.Responses, response => Assert.True(response.IsCompleted));
            }
            Assert.Equal(1, recorder.Count);
            Assert.Equal(cancelled ? ArticleBodyResult.Cancelled : ArticleBodyResult.NotFound, recorder.Result);
            Assert.Equal(0, inner.BatchRequestCount);
            Assert.Empty(inner.RequestedSegmentIds);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OmittedSegment_StatPipelinePreservesOrder()
    {
        var dir = Path.Join(Path.GetTempPath(), "omitted-stat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch("patched@example", [1], HeaderFor([1]));
            var marker = NzbFile.CreateOmittedSegmentId("first@example", 2);
            using var inner = new TrackingStatClient("normal");
            using var client = new RepairedSegmentNntpClient(inner, store);
            var results = new List<PipelinedStatResult>();
            await foreach (var result in client.StatsPipelinedAsync(["patched@example", marker, "remote@example"], 3, CancellationToken.None))
                results.Add(result);
            Assert.Equal(["patched@example", marker, "remote@example"], results.Select(result => result.SegmentId));
            Assert.Equal([true, false, true], results.Select(result => result.Exists));
            Assert.Equal(["remote@example"], inner.Requested);
            Assert.True(inner.Disposed);
            Assert.Equal(0, store.HitCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OmittedSegment_AttributionNeverProbesSyntheticId(bool exclusive)
    {
        var dir = Path.Join(Path.GetTempPath(), "omitted-attribution-" + Guid.NewGuid().ToString("N"));
        var previous = MultiProviderNntpClient.AttributionContext.Value;
        try
        {
            var store = new RepairPatchStore(dir, 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            var marker = NzbFile.CreateOmittedSegmentId("first@example", 2);
            store.CommitPatch(marker, [2], HeaderFor([2]));
            store.CommitPatch("real@example", [99], HeaderFor([99]));
            var inner = new FakeNntpClient(new Dictionary<string, byte[]> { ["real@example"] = [1] }, useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);
            MultiProviderNntpClient.AttributionContext.Value = new MultiProviderNntpClient.ResponderAttribution();
            var recorder = new ArticleBodyCompletionRecorder();
            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() => exclusive
                ? client.DecodedBodyAsync(marker, new UsenetExclusiveConnection(recorder.Invoke), CancellationToken.None)
                : client.DecodedBodyAsync(marker, recorder.Invoke, CancellationToken.None));
            Assert.Equal(1, recorder.Count);
            Assert.False((await client.StatAsync(marker, CancellationToken.None)).ArticleExists);
            Assert.Null(await client.TryGetLocalDecodedBodyAsync(marker, CancellationToken.None));
            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() => client.GetYencHeadersAsync(marker, CancellationToken.None));
            var batch = exclusive
                ? await client.DecodedBodiesAsync([marker, "real@example"], new UsenetExclusiveConnection(null), CancellationToken.None)
                : await client.DecodedBodiesAsync([marker, "real@example"], onConnectionReadyAgain: null, CancellationToken.None);
            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() => batch.Responses[0]);
            Assert.Equal(new byte[] { 1 }, await ReadOmittedResponseAsync(await batch.Responses[1]));
            await batch.Completion;
            var results = new List<PipelinedStatResult>();
            await foreach (var result in client.StatsPipelinedAsync([marker, "real@example"], 2, CancellationToken.None))
                results.Add(result);
            Assert.Equal([false, true], results.Select(result => result.Exists));
            Assert.Equal(["real@example"], inner.RequestedSegmentIds);
            Assert.Equal(["real@example"], inner.StatRequestOrder);
            Assert.Equal(0, store.HitCount);
        }
        finally
        {
            MultiProviderNntpClient.AttributionContext.Value = previous;
            Directory.Delete(dir, true);
        }
    }

    private static async Task<byte[]> ReadOmittedResponseAsync(UsenetDecodedBodyResponse response)
    {
        await using var stream = response.Stream!;
        await using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    [Fact]
    public async Task Stat_UsesUsablePatchWithoutHitMetrics_AndDropsMissingHeader()
    {
        var dir = Path.Join(Path.GetTempPath(), "par2-stat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch("local", [1], HeaderFor([1]));
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>());
            using var client = new RepairedSegmentNntpClient(inner, store);

            Assert.Equal(223, (await client.StatAsync("local", CancellationToken.None)).ResponseCode);
            Assert.Empty(inner.StatRequestOrder);
            Assert.Equal(0, store.HitCount);
            File.Delete(Assert.Single(Directory.GetFiles(dir, "*.h", SearchOption.AllDirectories)));
            Assert.False((await client.StatAsync("local", CancellationToken.None)).ArticleExists);
            Assert.False(store.Contains("local"));
            Assert.Equal(["local"], inner.StatRequestOrder);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stats_OnlyRequestsMissesInOriginalOrder(bool allLocal)
    {
        var dir = Path.Join(Path.GetTempPath(), "par2-stats-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch("local", [1], HeaderFor([1]));
            using var inner = new TrackingStatClient("normal");
            using var client = new RepairedSegmentNntpClient(inner, store);
            string[] ids = allLocal ? ["local", "local"] : ["local", "remote", "local", "remote"];
            var observed = new List<string>();
            await foreach (var result in client.StatsPipelinedAsync(ids, 2, CancellationToken.None))
                observed.Add(result.SegmentId);

            Assert.Equal(ids, observed);
            Assert.Equal(allLocal ? [] : ["remote", "remote"], inner.Requested);
            Assert.Equal(!allLocal, inner.Disposed);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("early")]
    [InlineData("mismatch")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("exception")]
    [InlineData("cancel")]
    public async Task Stats_DisposesRemoteIteratorOnEveryExit(string mode)
    {
        var dir = Path.Join(Path.GetTempPath(), "par2-stat-exit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            using var inner = new TrackingStatClient(mode);
            using var client = new RepairedSegmentNntpClient(inner, store);
            using var cancellation = new CancellationTokenSource();
            async Task ConsumeAsync()
            {
                await foreach (var result in client.StatsPipelinedAsync(["one", "two"], 2, cancellation.Token))
                {
                    if (mode == "early") break;
                    if (mode == "cancel") cancellation.Cancel();
                    Assert.NotNull(result);
                }
            }
            if (mode == "early") await ConsumeAsync();
            else if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(ConsumeAsync);
            else if (mode == "exception") await Assert.ThrowsAsync<IOException>(ConsumeAsync);
            else await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(ConsumeAsync);
            Assert.True(inner.Disposed);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stats_ProviderAttributionBypassesLocalPatch()
    {
        var dir = Path.Join(Path.GetTempPath(), "par2-stat-attribution-" + Guid.NewGuid().ToString("N"));
        var previous = MultiProviderNntpClient.AttributionContext.Value;
        try
        {
            var store = new RepairPatchStore(dir, 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch("local", [1], HeaderFor([1]));
            using var inner = new TrackingStatClient("normal");
            using var client = new RepairedSegmentNntpClient(inner, store);
            MultiProviderNntpClient.AttributionContext.Value = new MultiProviderNntpClient.ResponderAttribution();
            Assert.False((await client.StatAsync("local", CancellationToken.None)).ArticleExists);
            await foreach (var result in client.StatsPipelinedAsync(["local"], 2, CancellationToken.None))
                Assert.Equal("local", result.SegmentId);
            Assert.Equal(["local"], inner.Requested);
            Assert.True(inner.Disposed);
        }
        finally
        {
            MultiProviderNntpClient.AttributionContext.Value = previous;
            Directory.Delete(dir, true);
        }
    }

    private sealed class TrackingStatClient(string mode) : WrappingNntpClient(new FakeNntpClient(new Dictionary<string, byte[]>()))
    {
        public List<string> Requested { get; } = [];
        public bool Disposed { get; private set; }

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds, int depth, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requested.AddRange(segmentIds);
            try
            {
                await Task.CompletedTask;
                foreach (var id in segmentIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (mode == "missing") yield break;
                    if (mode == "exception") throw new IOException("scripted STAT failure");
                    yield return new PipelinedStatResult { SegmentId = mode == "mismatch" ? "wrong" : id, Exists = true };
                }
                if (mode == "extra")
                    yield return new PipelinedStatResult { SegmentId = "extra", Exists = true };
            }
            finally { Disposed = true; }
        }
    }

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
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
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
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
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

    [Fact]
    public async Task PatchedSegment_ThrowingCallbackStillReturnsPatch()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-patch-" + Guid.NewGuid().ToString("N"));
        const string segmentId = "patched-throw@test";
        byte[] content = "repaired-bytes"u8.ToArray();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch(segmentId, content, HeaderFor(content));

            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);

            var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
            var response = await client.DecodedBodyAsync(segmentId, recorder.Invoke, CancellationToken.None);
            Assert.NotNull(response.Stream);
            await using var output = new MemoryStream();
            await response.Stream.CopyToAsync(output);

            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
            Assert.Equal(0, inner.BodyRequestCount);
            Assert.Equal(content, output.ToArray());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PatchedSegment_ExclusiveThrowingCallbackStillReturnsPatch()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-patch-" + Guid.NewGuid().ToString("N"));
        const string segmentId = "patched-exclusive@test";
        byte[] content = "exclusive-repaired"u8.ToArray();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch(segmentId, content, HeaderFor(content));

            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);

            var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
            var exclusive = new UsenetExclusiveConnection(recorder.Invoke);
            var response = await client.DecodedBodyAsync(segmentId, exclusive, CancellationToken.None);
            Assert.NotNull(response.Stream);
            await using var output = new MemoryStream();
            await response.Stream.CopyToAsync(output);

            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
            Assert.Equal(0, inner.BodyRequestCount);
            Assert.Equal(content, output.ToArray());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedBodiesAsync_AllPatched_RequestsNoInnerBatch_AndCompletesOnce(bool exclusive)
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-patch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch("a@test", "aaa"u8.ToArray(), HeaderFor("aaa"u8.ToArray()));
            store.CommitPatch("b@test", "bbb"u8.ToArray(), HeaderFor("bbb"u8.ToArray()));
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);
            var recorder = new ArticleBodyCompletionRecorder();
            var ids = new SegmentId[] { "a@test", "b@test" };
            var batch = exclusive
                ? await client.DecodedBodiesAsync(ids, new UsenetExclusiveConnection(recorder.Invoke), CancellationToken.None)
                : await client.DecodedBodiesAsync(ids, recorder.Invoke, CancellationToken.None);

            Assert.Equal(0, inner.BatchRequestCount);
            await batch.DrainAsync();
            Assert.Equal(1, recorder.Count);
            Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedBodiesAsync_MixedPatchMissPatch_RequestsOnlyMiss(bool exclusive)
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-patch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch("a@test", "aaa"u8.ToArray(), HeaderFor("aaa"u8.ToArray()));
            store.CommitPatch("c@test", "ccc"u8.ToArray(), HeaderFor("ccc"u8.ToArray()));
            var inner = new FakeNntpClient(new Dictionary<string, byte[]> { ["b@test"] = "bbb"u8.ToArray() }, useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);
            var ids = new SegmentId[] { "a@test", "b@test", "c@test" };
            var batch = exclusive
                ? await client.DecodedBodiesAsync(ids, new UsenetExclusiveConnection(null), CancellationToken.None)
                : await client.DecodedBodiesAsync(ids, onConnectionReadyAgain: null, CancellationToken.None);

            await batch.DrainAsync();
            Assert.Equal(1, inner.BatchRequestCount);
            Assert.Equal(["b@test"], inner.RequestedSegmentIds.OrderBy(x => x).ToArray());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DecodedBodiesAsync_AttributionContext_BypassesPatchLookup()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-patch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch("a@test", "aaa"u8.ToArray(), HeaderFor("aaa"u8.ToArray()));
            var inner = new FakeNntpClient(new Dictionary<string, byte[]> { ["a@test"] = "remote"u8.ToArray() }, useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);
            MultiProviderNntpClient.AttributionContext.Value = new MultiProviderNntpClient.ResponderAttribution();
            try
            {
                var batch = await client.DecodedBodiesAsync(["a@test"], onConnectionReadyAgain: null, CancellationToken.None);
                await batch.DrainAsync();
            }
            finally
            {
                MultiProviderNntpClient.AttributionContext.Value = null;
            }

            Assert.Equal(1, inner.BatchRequestCount);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DecodedBodiesAsync_FetchAttributionContext_DoesNotBypassPatch()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-patch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            store.CommitPatch("a@test", "aaa"u8.ToArray(), HeaderFor("aaa"u8.ToArray()));
            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);
            using (FetchAttributionContext.Begin("movie.bin"))
            {
                var batch = await client.DecodedBodiesAsync(["a@test"], onConnectionReadyAgain: null, CancellationToken.None);
                await batch.DrainAsync();
            }

            Assert.Equal(0, inner.BatchRequestCount);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(ArticleBodyResult.Retrieved, null)]
    [InlineData(ArticleBodyResult.Cancelled, null)]
    [InlineData(ArticleBodyResult.NotFound, null)]
    [InlineData(ArticleBodyResult.NotRetrieved, "SocketException")]
    public async Task UnpatchedSegment_ForwardsTerminalStatusOnce(
        ArticleBodyResult result, string? failureReason)
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-patch-" + Guid.NewGuid().ToString("N"));
        const string segmentId = "unpatched@test";
        byte[] content = "provider-bytes"u8.ToArray();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            var inner = new ScriptedStatusNntpClient(result, failureReason, content);
            using var client = new RepairedSegmentNntpClient(inner, store);

            var recorder = new ArticleBodyCompletionRecorder();
            var response = await client.DecodedBodyAsync(segmentId, recorder.Invoke, CancellationToken.None);
            if (response.Stream != null)
            {
                await using (response.Stream)
                    await response.Stream.CopyToAsync(Stream.Null);
            }

            Assert.Equal(1, recorder.Count);
            Assert.Equal(result, recorder.Result);
            Assert.Equal(failureReason, recorder.FailureReason);
            Assert.Equal(1, inner.BodyRequestCount);
            Assert.Equal(1, inner.CompletionCount);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class ScriptedStatusNntpClient(
        ArticleBodyResult result,
        string? failureReason,
        byte[] content) : NntpClient
    {
        public int BodyRequestCount { get; private set; }
        public int CompletionCount { get; private set; }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            BodyRequestCount++;
            var success = result == ArticleBodyResult.Retrieved;
            var response = new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = success
                    ? (int)UsenetResponseType.ArticleRetrievedBodyFollows
                    : (int)UsenetResponseType.NoArticleWithThatMessageId,
                ResponseMessage = success ? "222" : "430",
                Stream = success ? CreateStream(content) : null,
            };
            onConnectionReadyAgain?.Invoke(result, failureReason);
            CompletionCount++;
            return Task.FromResult(response);
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }

        private static CachedYencStream CreateStream(byte[] bytes) =>
            new(
                HeaderFor(bytes),
                new MemoryStream(bytes, writable: false));
    }
}
