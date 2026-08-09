using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ArticleCachingNntpClientTests
{
    [SkippableFact]
    public async Task DecodedBodyAsync_CachesDecodedBytesAfterFirstRead()
    {
        Skip.IfNot(RapidYenc.IsAvailable, "rapidyenc native library not available on this platform");
        var inner = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["segment"] = Encoding.ASCII.GetBytes("cached payload")
        });
        using var client = new ArticleCachingNntpClient(inner);

        var first = await client.DecodedBodyAsync("segment", CancellationToken.None);
        var firstBytes = await ReadAllAsync(first.Stream!);
        var second = await client.DecodedBodyAsync("segment", CancellationToken.None);
        var secondBytes = await ReadAllAsync(second.Stream!);

        Assert.Equal("cached payload", Encoding.ASCII.GetString(firstBytes));
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(1, inner.BodyRequestCount);
    }

    [SkippableFact]
    public async Task DecodedBodiesAsync_PreservesOrderAcrossCachedAndMissingSegments()
    {
        Skip.IfNot(RapidYenc.IsAvailable, "rapidyenc native library not available on this platform");
        var inner = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["one"] = Encoding.ASCII.GetBytes("one"),
            ["two"] = Encoding.ASCII.GetBytes("two")
        });
        using var client = new ArticleCachingNntpClient(inner);
        var cached = await client.DecodedBodyAsync("one", CancellationToken.None);
        await ReadAllAsync(cached.Stream!);

        var batch = await client.DecodedBodiesAsync(
            ["one", "two"], onConnectionReadyAgain: null, CancellationToken.None);
        var responses = await Task.WhenAll(batch.Responses);
        var bodies = new List<string>();
        foreach (var response in responses)
            bodies.Add(Encoding.ASCII.GetString(await ReadAllAsync(response.Stream!)));

        Assert.Equal(new[] { "one", "two" }, bodies);
        Assert.Equal(1, inner.BatchRequestCount);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var destination = new MemoryStream();
            await stream.CopyToAsync(destination);
            return destination.ToArray();
        }
    }
}
