using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.TestUtils;

internal static class DecodedBodyBatchTestDrain
{
    public static async Task DrainAsync(this UsenetDecodedBodyBatch batch, TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(10);
        foreach (var responseTask in batch.Responses)
        {
            UsenetDecodedBodyResponse? response = null;
            try
            {
                response = await responseTask.WaitAsync(wait);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A faulted position still releases overlay readiness for later responses.
            }

            if (response?.Stream is not null)
            {
                try
                {
                    await response.Stream.CopyToAsync(Stream.Null);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Still dispose so overlay readiness and cache commit/skip can finish.
                }

                await response.Stream.DisposeAsync();
            }
        }

        await batch.Completion.WaitAsync(wait);
    }
}

internal sealed class ControlledDecodedBodyBatchClient : NntpClient
{
    public enum CallbackTiming
    {
        BeforeReturn,
        AfterReturn,
        Never,
        Twice,
    }

    private readonly Func<SegmentId, UsenetDecodedBodyResponse> _createResponse;
    private readonly int? _responseCountOverride;
    private readonly Exception? _setupException;
    private readonly Exception? _completionException;
    private readonly bool _blockCompletionUntilStreamsDisposed;
    private readonly IReadOnlyList<Task<UsenetDecodedBodyResponse>>? _responses;
    private readonly ConcurrentQueue<TaskCompletionSource> _completions = new();
    private int _disposedStreams;

    public ControlledDecodedBodyBatchClient(
        Func<SegmentId, UsenetDecodedBodyResponse>? createResponse = null,
        int? responseCountOverride = null,
        Exception? setupException = null,
        CallbackTiming callbackTiming = CallbackTiming.BeforeReturn,
        Exception? completionException = null,
        bool blockCompletionUntilStreamsDisposed = false,
        IReadOnlyList<Task<UsenetDecodedBodyResponse>>? responses = null)
    {
        _createResponse = createResponse ?? (id => CreateSuccess(id, "body"u8.ToArray()));
        _responseCountOverride = responseCountOverride;
        _setupException = setupException;
        _completionException = completionException;
        _blockCompletionUntilStreamsDisposed = blockCompletionUntilStreamsDisposed;
        _responses = responses;
        Timing = callbackTiming;
    }

    public CallbackTiming Timing { get; }
    public int OrdinaryBatchCount { get; private set; }
    public int ExclusiveBatchCount { get; private set; }
    public List<string> RequestedIds { get; } = [];
    public List<IReadOnlyList<string>> BatchIdLists { get; } = [];
    public ArticleBodyCompletionHandler? CapturedCallback { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }
    public Action? OnBatchStart { get; set; }
    public int DisposedStreamCount => Volatile.Read(ref _disposedStreams);
    public Task ProducerCompletion => _completions.LastOrDefault()?.Task ?? Task.CompletedTask;

    public void CompleteProducer()
    {
        foreach (var completion in _completions)
            completion.TrySetResult();
    }

    public void FaultProducer(Exception exception)
    {
        foreach (var completion in _completions)
            completion.TrySetException(exception);
    }

    public void FireCapturedCallback(ArticleBodyResult result = ArticleBodyResult.Retrieved, string? reason = null) =>
        CapturedCallback?.Invoke(result, reason);

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        OrdinaryBatchCount++;
        return CreateBatchAsync(segmentIds, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken)
    {
        ExclusiveBatchCount++;
        return CreateBatchAsync(segmentIds, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);
    }

    private Task<UsenetDecodedBodyBatch> CreateBatchAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnBatchStart?.Invoke();
        LastCancellationToken = cancellationToken;
        var ids = segmentIds.Select(id => id.ToString()).ToArray();
        RequestedIds.AddRange(ids);
        BatchIdLists.Add(ids);
        CapturedCallback = onConnectionReadyAgain;

        if (_setupException is not null)
            throw _setupException;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _completions.Enqueue(completion);

        var remainingHolder = new StrongBox<int>(0);
        void OnStreamDisposed()
        {
            Interlocked.Increment(ref _disposedStreams);
            if (Interlocked.Decrement(ref remainingHolder.Value) == 0)
                completion.TrySetResult();
        }

        IReadOnlyList<Task<UsenetDecodedBodyResponse>> responses;
        if (_responses is not null)
        {
            responses = _responses;
        }
        else
        {
            var count = _responseCountOverride ?? segmentIds.Count;
            var created = new Task<UsenetDecodedBodyResponse>[Math.Max(0, count)];
            for (var index = 0; index < created.Length; index++)
            {
                var id = index < segmentIds.Count ? segmentIds[index] : new SegmentId($"extra-{index}");
                var response = _createResponse(id);
                if (_blockCompletionUntilStreamsDisposed && response.Stream is not null)
                {
                    remainingHolder.Value++;
                    response = response with
                    {
                        Stream = new DisposeTrackingYencStream(response.Stream, OnStreamDisposed),
                    };
                }

                created[index] = Task.FromResult(response);
            }

            responses = created;
        }

        if (Timing is CallbackTiming.BeforeReturn or CallbackTiming.Twice)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        if (Timing == CallbackTiming.Twice)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved, "duplicate");

        if (_completionException is not null)
            completion.TrySetException(_completionException);
        else if (_blockCompletionUntilStreamsDisposed)
        {
            if (remainingHolder.Value == 0)
            {
                cancellationToken.Register(() => completion.TrySetResult());
                if (cancellationToken.IsCancellationRequested)
                    completion.TrySetResult();
            }
        }
        else if (Timing != CallbackTiming.Never && Timing != CallbackTiming.AfterReturn)
            completion.TrySetResult();

        return Task.FromResult(new UsenetDecodedBodyBatch
        {
            Responses = responses,
            Completion = completion.Task,
        });
    }

    public static UsenetDecodedBodyResponse CreateSuccess(SegmentId segmentId, byte[] content)
    {
        return new UsenetDecodedBodyResponse
        {
            SegmentId = segmentId.ToString(),
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
            ResponseMessage = "222",
            Stream = new CachedYencStream(HeaderFor(content), new MemoryStream(content, writable: false)),
        };
    }

    public static UsenetDecodedBodyResponse CreateMissing(SegmentId segmentId) =>
        new()
        {
            SegmentId = segmentId.ToString(),
            ResponseCode = (int)UsenetResponseType.NoArticleWithThatMessageId,
            ResponseMessage = "430",
            Stream = null,
        };

    public static UsenetYencHeader HeaderFor(byte[] content) => new()
    {
        FileName = "test.bin",
        FileSize = content.Length,
        LineLength = 128,
        PartNumber = 1,
        TotalParts = 1,
        PartOffset = 0,
        PartSize = content.Length,
    };

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
        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        return Task.FromResult(_createResponse(segmentId));
    }

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

    private sealed class DisposeTrackingYencStream : YencStream
    {
        private readonly YencStream _inner;
        private readonly Action _onDisposed;
        private int _disposed;

        public DisposeTrackingYencStream(YencStream inner, Action onDisposed) : base(Null)
        {
            _inner = inner;
            _onDisposed = onDisposed;
        }

        public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(
            CancellationToken cancellationToken = default) =>
            _inner.GetYencHeadersAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                SignalDisposed();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            SignalDisposed();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private void SignalDisposed()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _onDisposed();
        }
    }
}
