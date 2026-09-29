using System.Collections.Concurrent;
using UsenetSharp.Clients;
using UsenetSharp.Models;
using UsenetSharpTest.Support;

namespace UsenetSharpTest.Protocol;

[TestFixture]
public class UsenetClientPayloadObserverTests
{
    private const int BurstLines = 2_500;
    private static readonly string BurstLine = new string('x', 126) + "\r\n";

    [Test]
    public async Task BodyAsync_ReportsEveryLineInCoalescedChunks()
    {
        var body = string.Concat(Enumerable.Repeat(BurstLine, BurstLines));
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
        {
            await writer.WriteAsync("222 body follows\r\n" + body + ".\r\n");
        });
        var calls = new ConcurrentQueue<int>();
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            PayloadBytesObserver = calls.Enqueue,
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var response = await client.BodyAsync(
            "article@example.com",
            (result, _) => completion.TrySetResult(result),
            CancellationToken.None);
        await response.Stream!.CopyToAsync(Stream.Null);

        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            Is.EqualTo(ArticleBodyResult.Retrieved));
        AssertCoalesced(calls, body.Length);
    }

    [Test]
    public async Task CancelledDecodedBody_DrainReportsEveryLineInCoalescedChunks()
    {
        var header = "=ybegin line=128 size=320000 name=test.bin\r\n";
        var burst = string.Concat(Enumerable.Repeat(BurstLine, BurstLines)) + "=yend size=320000\r\n";
        var releaseBurst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedNntpServer(async (_, writer, _) =>
        {
            await writer.WriteAsync("222 body follows\r\n" + header);
            await releaseBurst.Task;
            await writer.WriteAsync(burst + ".\r\n");
        });
        var calls = new ConcurrentQueue<int>();
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            PayloadBytesObserver = calls.Enqueue,
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
        var completion = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();

        await client.DecodedBodyAsync(
            "article@example.com",
            (result, _) => completion.TrySetResult(result),
            cancel.Token);
        await cancel.CancelAsync();
        releaseBurst.SetResult();

        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            Is.EqualTo(ArticleBodyResult.Cancelled));
        AssertCoalesced(calls, header.Length + burst.Length, extraFinalReports: 1);
    }

    private static void AssertCoalesced(
        IReadOnlyCollection<int> calls,
        int expectedBytes,
        int extraFinalReports = 0)
    {
        Assert.That(calls.Sum(), Is.EqualTo(expectedBytes));
        Assert.That(calls.Count, Is.GreaterThan(0));
        Assert.That(
            calls.Count,
            Is.LessThanOrEqualTo(expectedBytes / PayloadBytesObserver.CoalesceThreshold + 1 + extraFinalReports),
            "payload should be reported per chunk, not per wire line");
    }
}
