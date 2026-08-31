using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class LocalDataBatchOverlayTests
{
    [Fact]
    public async Task AllLocal_PublishesResponsesOnlyAfterPreviousStreamTerminal()
    {
        var ids = Ids("a", "b");
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            ids,
            outerCallback: null,
            id => LocalLookupResult.Hit(Local(id)),
            FetchShouldNotRun,
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        var first = await batch.Responses[0].WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(batch.Responses[1].IsCompleted);
        await first.Stream!.DisposeAsync();
        var second = await batch.Responses[1].WaitAsync(TimeSpan.FromSeconds(5));
        await second.Stream!.DisposeAsync();
        await batch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AllRemote_PreservesInnerResponseAndCompletionOrder()
    {
        var inner = new ControlledDecodedBodyBatchClient();
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a", "b"),
            recorder.Invoke,
            _ => LocalLookupResult.Miss,
            (misses, callback, token) => inner.DecodedBodiesAsync(misses, callback, token),
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        Assert.Equal(1, inner.OrdinaryBatchCount);
        Assert.Equal(["a", "b"], inner.RequestedIds);
        var first = await batch.Responses[0].WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(batch.Responses[1].IsCompleted);
        await first.Stream!.DisposeAsync();
        var second = await batch.Responses[1].WaitAsync(TimeSpan.FromSeconds(5));
        await second.Stream!.DisposeAsync();
        await batch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
    }

    [Fact]
    public async Task MixedLocalRemoteLocal_RequestsOnlyRemoteAndPreservesPositions()
    {
        var inner = new ControlledDecodedBodyBatchClient();
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a", "b", "a"),
            recorder.Invoke,
            id => id.ToString() == "a"
                ? LocalLookupResult.Hit(Local(id, "local-a"u8.ToArray()))
                : LocalLookupResult.Miss,
            (misses, callback, token) => inner.DecodedBodiesAsync(misses, callback, token),
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        Assert.Equal(1, inner.OrdinaryBatchCount);
        Assert.Equal(["b"], inner.RequestedIds);
        await batch.DrainAsync();
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
    }

    [Fact]
    public async Task DuplicateIds_PreserveEveryOriginalPosition()
    {
        var seen = new List<string>();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a", "a"),
            outerCallback: null,
            id =>
            {
                seen.Add(id.ToString());
                return LocalLookupResult.Hit(Local(id, "dup"u8.ToArray()));
            },
            FetchShouldNotRun,
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        Assert.Equal(["a", "a"], seen);
        var first = await batch.Responses[0];
        await first.Stream!.DisposeAsync();
        var second = await batch.Responses[1];
        Assert.NotSame(first.Stream, second.Stream);
        await second.Stream!.DisposeAsync();
        await batch.Completion;
    }

    [Fact]
    public async Task InnerCallbackBeforeReturn_IsDeferredAndForwardedOnce()
    {
        var inner = new ControlledDecodedBodyBatchClient(callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.BeforeReturn);
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a"),
            recorder.Invoke,
            _ => LocalLookupResult.Miss,
            (misses, callback, token) => inner.DecodedBodiesAsync(misses, callback, token),
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        Assert.Equal(0, recorder.Count);
        await batch.DrainAsync();
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
    }

    [Fact]
    public async Task InnerCallbackAfterActivation_IsForwardedOnce()
    {
        var inner = new ControlledDecodedBodyBatchClient(callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.AfterReturn);
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a"),
            recorder.Invoke,
            _ => LocalLookupResult.Miss,
            (misses, callback, token) => inner.DecodedBodiesAsync(misses, callback, token),
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        Assert.Equal(0, recorder.Count);
        inner.FireCapturedCallback(ArticleBodyResult.Retrieved);
        inner.CompleteProducer();
        await batch.DrainAsync();
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
    }

    [Fact]
    public async Task DuplicateInnerCallback_FirstTerminalResultWins()
    {
        var inner = new ControlledDecodedBodyBatchClient(callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.Twice);
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a"),
            recorder.Invoke,
            _ => LocalLookupResult.Miss,
            (misses, callback, token) => inner.DecodedBodiesAsync(misses, callback, token),
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        await batch.DrainAsync();
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.Retrieved, recorder.Result);
    }

    [Fact]
    public async Task ThrowingOuterCallback_DoesNotFaultResponsesOrCompletion()
    {
        var recorder = new ArticleBodyCompletionRecorder(throwOnInvoke: true);
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a"),
            recorder.Invoke,
            id => LocalLookupResult.Hit(Local(id)),
            FetchShouldNotRun,
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        await batch.DrainAsync();
        Assert.Equal(1, recorder.Count);
        Assert.True(batch.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task InnerSetupThrow_CleansLocalStreamsAndReportsNotRetrievedOnce()
    {
        var recorder = new ArticleBodyCompletionRecorder();
        var local = Local("a");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalDataBatchOverlay.ExecuteAsync(
                Ids("a", "b"),
                recorder.Invoke,
                id => id.ToString() == "a" ? LocalLookupResult.Hit(local) : LocalLookupResult.Miss,
                (_, _, _) => throw new InvalidOperationException("batch-setup"),
                LocalDataBatchOverlay.PassThroughRemote,
                CancellationToken.None));

        Assert.Equal("batch-setup", error.Message);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
        Assert.True(IsDisposed(local.Stream!));
    }

    [Fact]
    public async Task ResponseCountTooSmall_DrainsInnerAndReportsNotRetrievedOnce()
    {
        var inner = new ControlledDecodedBodyBatchClient(responseCountOverride: 0);
        var recorder = new ArticleBodyCompletionRecorder();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalDataBatchOverlay.ExecuteAsync(
                Ids("a", "b"),
                recorder.Invoke,
                _ => LocalLookupResult.Miss,
                (misses, callback, token) => inner.DecodedBodiesAsync(misses, callback, token),
                LocalDataBatchOverlay.PassThroughRemote,
                CancellationToken.None));

        Assert.Contains("returned 0 responses for 2 requests", error.Message);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
        Assert.Equal("batch-response-count-mismatch", recorder.FailureReason);
    }

    [Fact]
    public async Task ResponseCountTooLarge_DrainsInnerAndReportsNotRetrievedOnce()
    {
        var inner = new ControlledDecodedBodyBatchClient(responseCountOverride: 3);
        var recorder = new ArticleBodyCompletionRecorder();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalDataBatchOverlay.ExecuteAsync(
                Ids("a", "b"),
                recorder.Invoke,
                _ => LocalLookupResult.Miss,
                (misses, callback, token) => inner.DecodedBodiesAsync(misses, callback, token),
                LocalDataBatchOverlay.PassThroughRemote,
                CancellationToken.None));

        Assert.Contains("returned 3 responses for 2 requests", error.Message);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
    }

    [Fact]
    public async Task ResponseTaskFailure_DoesNotStrandLaterPositions()
    {
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a", "b"),
            recorder.Invoke,
            _ => LocalLookupResult.Miss,
            (misses, callback, token) =>
            {
                callback(ArticleBodyResult.Retrieved);
                return Task.FromResult(new UsenetDecodedBodyBatch
                {
                    Responses =
                    [
                        Task.FromException<UsenetDecodedBodyResponse>(new IOException("remote-fail")),
                        Task.FromResult(ControlledDecodedBodyBatchClient.CreateSuccess(misses[1], "ok"u8.ToArray())),
                    ],
                    Completion = Task.CompletedTask,
                });
            },
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => batch.Responses[0]);
        var second = await batch.Responses[1].WaitAsync(TimeSpan.FromSeconds(5));
        await second.Stream!.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(() => batch.Completion);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
    }

    [Fact]
    public async Task StreamReadFailure_ReleasesNextReadinessGate()
    {
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a", "b"),
            recorder.Invoke,
            id => LocalLookupResult.Hit(new UsenetDecodedBodyResponse
            {
                SegmentId = id.ToString(),
                ResponseCode = 222,
                ResponseMessage = "222",
                Stream = id.ToString() == "a"
                    ? new ThrowingReadYencStream()
                    : new CachedYencStream(
                        ControlledDecodedBodyBatchClient.HeaderFor("b"u8.ToArray()),
                        new MemoryStream("b"u8.ToArray(), writable: false)),
            }),
            FetchShouldNotRun,
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        var first = await batch.Responses[0];
        await Assert.ThrowsAsync<IOException>(async () => await first.Stream!.ReadAsync(new byte[8]));
        var second = await batch.Responses[1].WaitAsync(TimeSpan.FromSeconds(5));
        await second.Stream!.DisposeAsync();
        await first.Stream!.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(() => batch.Completion);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
    }

    [Fact]
    public async Task StreamDisposeFailure_ReleasesNextReadinessGate()
    {
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a", "b"),
            recorder.Invoke,
            id => LocalLookupResult.Hit(new UsenetDecodedBodyResponse
            {
                SegmentId = id.ToString(),
                ResponseCode = 222,
                ResponseMessage = "222",
                Stream = id.ToString() == "a"
                    ? new ThrowingDisposeYencStream()
                    : new CachedYencStream(
                        ControlledDecodedBodyBatchClient.HeaderFor("b"u8.ToArray()),
                        new MemoryStream("b"u8.ToArray(), writable: false)),
            }),
            FetchShouldNotRun,
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        var first = await batch.Responses[0];
        await Assert.ThrowsAsync<IOException>(async () => await first.Stream!.DisposeAsync());
        var second = await batch.Responses[1].WaitAsync(TimeSpan.FromSeconds(5));
        await second.Stream!.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(() => batch.Completion);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
    }

    [Fact]
    public async Task InnerCompletionFailure_IsObservedAndSurfaced()
    {
        var inner = new ControlledDecodedBodyBatchClient(
            callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.AfterReturn);
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a"),
            recorder.Invoke,
            _ => LocalLookupResult.Miss,
            (misses, callback, token) => inner.DecodedBodiesAsync(misses, callback, token),
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        inner.FireCapturedCallback(ArticleBodyResult.Retrieved);
        inner.FaultProducer(new IOException("inner-completion"));
        var response = await batch.Responses[0];
        await response.Stream!.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(() => batch.Completion);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
    }

    [Fact]
    public async Task FailureAfterActivation_DowngradesEarlyRetrievedToNotRetrieved()
    {
        var inner = new ControlledDecodedBodyBatchClient(
            callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.BeforeReturn);
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a"),
            recorder.Invoke,
            _ => LocalLookupResult.Miss,
            (misses, callback, token) => inner.DecodedBodiesAsync(misses, callback, token),
            async (id, response, _) =>
            {
                await response.Stream!.DisposeAsync();
                throw new IOException("transform-fail");
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => batch.Responses[0]);
        await Assert.ThrowsAsync<IOException>(() => batch.Completion);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
    }

    [Fact]
    public async Task ConcurrentDuplicateInnerCallbacks_ForwardOneCoherentPair()
    {
        var inner = new ControlledDecodedBodyBatchClient(
            callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.AfterReturn);
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a"),
            recorder.Invoke,
            _ => LocalLookupResult.Miss,
            (misses, callback, token) => inner.DecodedBodiesAsync(misses, callback, token),
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        await Task.WhenAll(
            Task.Run(() => inner.FireCapturedCallback(ArticleBodyResult.Retrieved)),
            Task.Run(() => inner.FireCapturedCallback(ArticleBodyResult.NotRetrieved, "duplicate")));
        inner.CompleteProducer();
        await batch.DrainAsync();
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task EarlyBatchAbandonment_CancelDisposeAwait_ReleasesEverything()
    {
        var recorder = new ArticleBodyCompletionRecorder();
        using var cts = new CancellationTokenSource();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a", "b", "c"),
            recorder.Invoke,
            id => LocalLookupResult.Hit(Local(id)),
            FetchShouldNotRun,
            LocalDataBatchOverlay.PassThroughRemote,
            cts.Token);

        var first = await batch.Responses[0];
        await first.Stream!.DisposeAsync();
        await cts.CancelAsync();
        for (var index = 1; index < batch.Responses.Count; index++)
        {
            var response = await batch.Responses[index].WaitAsync(TimeSpan.FromSeconds(5));
            if (response.Stream is not null)
                await response.Stream.DisposeAsync();
        }

        await batch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task MissingInnerCallback_CompletesAsNotRetrievedWithoutHanging()
    {
        var inner = new ControlledDecodedBodyBatchClient(callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.Never);
        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a"),
            recorder.Invoke,
            _ => LocalLookupResult.Miss,
            (misses, callback, token) => inner.DecodedBodiesAsync(misses, callback, token),
            LocalDataBatchOverlay.PassThroughRemote,
            CancellationToken.None);

        inner.CompleteProducer();
        await batch.DrainAsync();
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
        Assert.Equal("inner-callback-missing", recorder.FailureReason);
    }

    [Fact]
    public async Task CancellationBeforePartition_ReportsOnceAndOpensNothing()
    {
        var recorder = new ArticleBodyCompletionRecorder();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var opened = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalDataBatchOverlay.ExecuteAsync(
                Ids("a"),
                recorder.Invoke,
                _ =>
                {
                    opened++;
                    return LocalLookupResult.Miss;
                },
                FetchShouldNotRun,
                LocalDataBatchOverlay.PassThroughRemote,
                cts.Token));

        Assert.Equal(0, opened);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.Cancelled, recorder.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationAtEachPosition_ReleasesAllGates(int cancelIndex)
    {
        var recorder = new ArticleBodyCompletionRecorder();
        using var cts = new CancellationTokenSource();
        var batch = await LocalDataBatchOverlay.ExecuteAsync(
            Ids("a", "b", "c"),
            recorder.Invoke,
            id => LocalLookupResult.Hit(Local(id)),
            FetchShouldNotRun,
            LocalDataBatchOverlay.PassThroughRemote,
            cts.Token);

        for (var index = 0; index < batch.Responses.Count; index++)
        {
            var response = await batch.Responses[index].WaitAsync(TimeSpan.FromSeconds(5));
            if (index == cancelIndex)
                await cts.CancelAsync();
            if (response.Stream is not null)
                await response.Stream.DisposeAsync();
        }

        await batch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task EmptyList_ThrowsBeforeCallbackOrStore()
    {
        var recorder = new ArticleBodyCompletionRecorder();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            LocalDataBatchOverlay.ExecuteAsync(
                [],
                recorder.Invoke,
                _ => throw new InvalidOperationException("should not open"),
                FetchShouldNotRun,
                LocalDataBatchOverlay.PassThroughRemote,
                CancellationToken.None));
        Assert.Equal(0, recorder.Count);
    }

    private static SegmentId[] Ids(params string[] ids) => ids.Select(id => new SegmentId(id)).ToArray();

    private static UsenetDecodedBodyResponse Local(SegmentId id, byte[]? content = null) =>
        ControlledDecodedBodyBatchClient.CreateSuccess(id, content ?? "local"u8.ToArray());

    private static Task<UsenetDecodedBodyBatch> FetchShouldNotRun(
        IReadOnlyList<SegmentId> _,
        ArticleBodyCompletionHandler __,
        CancellationToken ___) =>
        throw new InvalidOperationException("inner batch should not run");

    private static bool IsDisposed(Stream stream)
    {
        try
        {
            _ = stream.ReadByte();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private sealed class ThrowingReadYencStream : YencStream
    {
        public ThrowingReadYencStream() : base(Null)
        {
        }

        public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<UsenetYencHeader?>(ControlledDecodedBodyBatchClient.HeaderFor("x"u8.ToArray()));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("read-fail");
    }

    private sealed class ThrowingDisposeYencStream : YencStream
    {
        public ThrowingDisposeYencStream() : base(Null)
        {
        }

        public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<UsenetYencHeader?>(ControlledDecodedBodyBatchClient.HeaderFor("x"u8.ToArray()));

        public override ValueTask DisposeAsync() => throw new IOException("dispose-fail");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                throw new IOException("dispose-fail");
            base.Dispose(disposing);
        }
    }
}
