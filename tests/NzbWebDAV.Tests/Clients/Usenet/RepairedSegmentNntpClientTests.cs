using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class RepairedSegmentNntpClientTests
{
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

            Assert.Equal(1, inner.BatchRequestCount);
            Assert.Equal(["b@test"], inner.RequestedSegmentIds.OrderBy(x => x).ToArray());
            await batch.DrainAsync();
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
