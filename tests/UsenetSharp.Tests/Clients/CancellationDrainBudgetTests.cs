using UsenetSharp.Clients;
using UsenetSharp.Models;
using UsenetSharpTest.Support;

namespace UsenetSharpTest.Protocol;

[TestFixture]
public class CancellationDrainBudgetTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task SlowCancelledBody_DeadlinePoisonsAndCompletesExactlyOnce(bool raw)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = ScriptedNntpServer.StartConnectionScript(async (reader, writer, ct) =>
        {
            _ = await reader.ReadLineAsync(ct);
            await writer.WriteAsync("222 body follows\r\n=ybegin line=128 size=2 name=data.bin\r\nk\r\n");
            sent.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(5),
            CancellationDrainTimeoutResolver = _ => TimeSpan.FromMilliseconds(100)
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var completed = new TaskCompletionSource<ArticleBodyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        void OnComplete(ArticleBodyResult result, string? reason)
        {
            Interlocked.Increment(ref callbacks);
            completed.TrySetResult(result);
        }
        var bodyStream = raw
            ? (await client.BodyAsync("data@example", OnComplete, cts.Token)).Stream!
            : (await client.DecodedBodyAsync("data@example", OnComplete, cts.Token)).Stream!;
        var copy = bodyStream.CopyToAsync(Stream.Null);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        Assert.That(await completed.Task.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(ArticleBodyResult.NotRetrieved));
        await Assert.CatchAsync<OperationCanceledException>(async () => await copy.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(client.IsHealthy, Is.False);
        Assert.That(callbacks, Is.EqualTo(1));
    }

    [Test]
    public async Task DrainDeadline_DoesNotApplyToHealthyTransfer()
    {
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
            await writer.WriteAsync("222 body follows\r\n=ybegin line=128 size=2 name=data.bin\r\nkl\r\n=yend size=2\r\n.\r\n"));
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            CancellationDrainTimeoutResolver = _ => TimeSpan.Zero
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completed = new TaskCompletionSource<ArticleBodyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = await client.DecodedBodyAsync("data@example", (result, _) => completed.TrySetResult(result), CancellationToken.None);
        using var bytes = new MemoryStream();
        await body.Stream!.CopyToAsync(bytes);
        Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 65, 66 }));
        Assert.That(await completed.Task.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(ArticleBodyResult.Retrieved));
        Assert.That(client.IsHealthy, Is.True);
    }

    [Test]
    public async Task FastCancelledBody_DrainsAndReusesConnection()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = ScriptedNntpServer.StartConnectionScript(async (reader, writer, ct) =>
        {
            _ = await reader.ReadLineAsync(ct);
            await writer.WriteAsync("222 body follows\r\n=ybegin line=128 size=2 name=data.bin\r\nk\r\n");
            await release.Task.WaitAsync(ct);
            await writer.WriteAsync("l\r\n=yend size=2\r\n.\r\n");
            Assert.That(await reader.ReadLineAsync(ct), Is.EqualTo("DATE"));
            await writer.WriteLineAsync("111 20260709213000");
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(5),
            CancellationDrainTimeoutResolver = _ => TimeSpan.FromSeconds(1)
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var completed = new TaskCompletionSource<ArticleBodyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var body = await client.DecodedBodyAsync("data@example", (result, _) =>
        {
            Interlocked.Increment(ref callbacks);
            completed.TrySetResult(result);
        }, cts.Token);
        var copy = body.Stream!.CopyToAsync(Stream.Null);
        cts.Cancel();
        release.TrySetResult();
        Assert.That(await completed.Task.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(ArticleBodyResult.Cancelled));
        await Assert.CatchAsync<OperationCanceledException>(async () => await copy.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(client.IsHealthy, Is.True);
        Assert.That((await client.DateAsync(CancellationToken.None)).ResponseCode, Is.EqualTo(111));
        Assert.That(callbacks, Is.EqualTo(1));
    }

    [Test]
    public async Task CancelledBatch_DeadlineIncludesCurrentAndQueuedBodies()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = ScriptedNntpServer.StartConnectionScript(async (reader, writer, ct) =>
        {
            _ = await reader.ReadLineAsync(ct);
            _ = await reader.ReadLineAsync(ct);
            await writer.WriteAsync("222 body follows\r\n=ybegin line=128 size=2 name=data.bin\r\nk\r\n");
            await releaseFirst.Task.WaitAsync(ct);
            await writer.WriteAsync("l\r\n=yend size=2\r\n.\r\n222 body follows\r\n=ybegin line=128 size=2 name=next.bin\r\nk\r\n");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(5),
            CancellationDrainTimeoutResolver = _ => TimeSpan.FromMilliseconds(100)
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var completed = new TaskCompletionSource<ArticleBodyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var batch = await client.DecodedBodiesAsync(new SegmentId[] { "first@example", "second@example" },
            (result, _) => { Interlocked.Increment(ref callbacks); completed.TrySetResult(result); }, cts.Token);
        var body = await batch.Responses[0];
        var copy = body.Stream!.CopyToAsync(Stream.Null);
        cts.Cancel();
        releaseFirst.TrySetResult();
        Assert.That(await completed.Task.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(ArticleBodyResult.NotRetrieved));
        await Assert.CatchAsync<OperationCanceledException>(async () => await copy.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.CatchAsync<OperationCanceledException>(async () => await batch.Responses[1]);
        Assert.That(client.IsHealthy, Is.False);
        Assert.That(callbacks, Is.EqualTo(1));
    }
}
