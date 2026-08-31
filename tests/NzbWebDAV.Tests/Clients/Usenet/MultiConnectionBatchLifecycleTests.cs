using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class MultiConnectionBatchLifecycleTests
{
    [Fact]
    public async Task InnerCompletionOom_WaitsForCallbackBeforeReleasingConnection()
    {
        var oom = new OutOfMemoryException("inner-completion");
        var inner = new ControlledDecodedBodyBatchClient(
            callbackTiming: ControlledDecodedBodyBatchClient.CallbackTiming.AfterReturn,
            completionException: oom);
        using var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult<INntpClient>(inner));
        using var client = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("batch-oom"),
            "batch-oom");

        var recorder = new ArticleBodyCompletionRecorder();
        var batch = await client.DecodedBodiesAsync(["a@test"], recorder.Invoke, CancellationToken.None);
        Assert.Equal(0, client.AvailableConnections);
        Assert.False(batch.Completion.IsCompleted);

        inner.FireCapturedCallback(ArticleBodyResult.NotRetrieved, "out-of-memory");
        var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(
            () => batch.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(oom, thrown);
        Assert.Equal(1, client.AvailableConnections);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(ArticleBodyResult.NotRetrieved, recorder.Result);
    }
}
