using System.Security.Authentication;
using UsenetSharp.Clients;
using UsenetSharp.Models;
using UsenetSharpTest.Support;

namespace UsenetSharpTest.Protocol;

[TestFixture]
public sealed class UsenetClientLoopbackBodyTests
{
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    public async Task DecodedBodiesAsync_PlaintextBatchPreservesOrderAndCompletesOnce(int width)
    {
        var expected = Enumerable.Range(0, width)
            .Select(index => Enumerable.Repeat((byte)(index + 1), 4096 + index).ToArray())
            .ToArray();
        var bodies = expected
            .Select((payload, index) => YencWireBodies.SinglePart(payload, $"loopback-{index}.bin"))
            .ToArray();
        var requestIndex = 0;
        await using var server = new ScriptedNntpServer(async (command, writer, cancellationToken) =>
        {
            Assert.That(command, Is.EqualTo($"BODY <loopback-{requestIndex}@example.com>"));
            var body = bodies[requestIndex++];
            await writer.WriteLineAsync("222 body follows");
            await writer.FlushAsync();
            await writer.BaseStream.WriteAsync(body.Wire, cancellationToken);
            await writer.BaseStream.FlushAsync(cancellationToken);
        });
        await using var client = new UsenetClient(new UsenetClientOptions
        {
            CrcValidation = YencCrcValidationMode.Require,
            MaxPipelineDepth = 8,
        });
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var outcomes = new List<ArticleBodyResult>();
        var ids = Enumerable.Range(0, width)
            .Select(index => new SegmentId($"loopback-{index}@example.com"))
            .ToArray();
        var batch = await client.DecodedBodiesAsync(
            ids,
            (result, _) => outcomes.Add(result),
            CancellationToken.None);

        for (var index = 0; index < width; index++)
        {
            var response = await batch.Responses[index];
            Assert.That(response.SegmentId.ToString(), Is.EqualTo(ids[index].ToString()));
            await using var body = response.Stream!;
            using var output = new MemoryStream();
            await body.CopyToAsync(output);
            Assert.That(output.ToArray(), Is.EqualTo(expected[index]));
        }

        await batch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(server.Commands.Count(command => command.StartsWith("BODY ", StringComparison.Ordinal)), Is.EqualTo(width));
            Assert.That(outcomes, Is.EqualTo(new[] { ArticleBodyResult.Retrieved }));
            Assert.That(client.IsHealthy, Is.True);
        });
    }

    [Test]
    public async Task DecodedBodiesAsync_CleanMissingArticleCompletesNotFound()
    {
        await using var server = new ScriptedNntpServer(async (command, writer, _) =>
        {
            Assert.That(command, Is.EqualTo("BODY <missing@example.com>"));
            await writer.WriteLineAsync("430 no such article");
        });
        await using var client = new UsenetClient();
        await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);

        var outcome = new TaskCompletionSource<ArticleBodyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = await client.DecodedBodiesAsync(
            [new SegmentId("missing@example.com")],
            (result, _) => outcome.TrySetResult(result),
            CancellationToken.None);

        var response = await batch.Responses[0];
        var completedOutcome = await outcome.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(response.ResponseCode, Is.EqualTo(430));
            Assert.That(response.Stream, Is.Null);
            Assert.That(completedOutcome, Is.EqualTo(ArticleBodyResult.NotFound));
        });
        await batch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ConnectAsync_RejectsUntrustedTlsLoopbackCertificate()
    {
        await using var server = ScriptedNntpServer.StartTlsConnectionScript((_, _, _) => Task.CompletedTask);
        await using var client = new UsenetClient();

        Assert.ThrowsAsync<AuthenticationException>(() =>
            client.ConnectAsync("127.0.0.1", server.Port, true, CancellationToken.None));
    }
}
