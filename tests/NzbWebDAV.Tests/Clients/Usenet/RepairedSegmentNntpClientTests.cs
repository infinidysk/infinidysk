using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
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

    [Fact]
    public async Task PatchedSegment_ThrowingCallbackStillReturnsPatch()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-repair-patch-" + Guid.NewGuid().ToString("N"));
        const string segmentId = "patched-throw@test";
        byte[] content = "repaired-bytes"u8.ToArray();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.CatalogLoadTask;
            store.CommitPatch(segmentId, content, HeaderFor(content));

            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);

            var recorder = new CompletionRecorder { ThrowOnInvoke = true };
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
            await store.CatalogLoadTask;
            store.CommitPatch(segmentId, content, HeaderFor(content));

            var inner = new FakeNntpClient(new Dictionary<string, byte[]>(), useCachedYencStreams: true);
            using var client = new RepairedSegmentNntpClient(inner, store);

            var recorder = new CompletionRecorder { ThrowOnInvoke = true };
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
            await store.CatalogLoadTask;
            var inner = new ScriptedStatusNntpClient(result, failureReason, content);
            using var client = new RepairedSegmentNntpClient(inner, store);

            var recorder = new CompletionRecorder();
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

    private sealed class CompletionRecorder
    {
        public int Count;
        public ArticleBodyResult? Result;
        public string? FailureReason;
        public bool ThrowOnInvoke;

        public void Invoke(ArticleBodyResult result, string? failureReason)
        {
            Count++;
            Result = result;
            FailureReason = failureReason;
            if (ThrowOnInvoke)
                throw new InvalidOperationException("callback failure");
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
