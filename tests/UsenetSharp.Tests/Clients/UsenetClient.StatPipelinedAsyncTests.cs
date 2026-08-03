using UsenetSharp.Clients;
using UsenetSharp.Models;

namespace UsenetSharpTest.Clients;

public class StatPipelinedAsyncTests
{
    private static readonly string[] ValidSegmentIds =
    [
        "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo",
        "439e9msqibLI2Ckyt7z6EMUY@iLqyzimBg7ac.t2n",
        "RdmCrBrzTPnTiYj7ze5Gwyt6@mfdz3EInI86g.bfV",
        "njX6awmG5Rl0lZbBbfll8WtA@M6zC3hmaiMoK.w5x",
        "vAOEczfxpsXMjg0bUPUGO7Bb@KDqE994Bw3O0.BG5"
    ];

    [Test]
    public async Task StatPipelinedAsync_WithValidSegmentIds_ReturnsAllPresent()
    {
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);
        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var segmentIds = ValidSegmentIds.Select(id => (SegmentId)id).ToArray();
        var results = await client.StatPipelinedAsync(segmentIds, cancellationToken);

        Assert.That(results, Has.Count.EqualTo(ValidSegmentIds.Length));
        for (var index = 0; index < results.Count; index++)
        {
            Assert.That(results[index].ArticleExists, Is.True,
                $"Article should exist for {ValidSegmentIds[index]}");
            Assert.That(results[index].ResponseCode, Is.EqualTo(223));
        }
    }

    [Test]
    public async Task StatPipelinedAsync_WithInterleavedMissingId_MapsPerPosition()
    {
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);
        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var segmentIds = new SegmentId[]
        {
            ValidSegmentIds[0],
            "invalid@segment.id",
            ValidSegmentIds[1]
        };
        var results = await client.StatPipelinedAsync(segmentIds, cancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results[0].ArticleExists, Is.True);
            Assert.That(results[0].ResponseCode, Is.EqualTo(223));
            Assert.That(results[1].ArticleExists, Is.False);
            Assert.That(results[1].ResponseCode, Is.EqualTo(430));
            Assert.That(results[2].ArticleExists, Is.True);
            Assert.That(results[2].ResponseCode, Is.EqualTo(223));
        });
    }

    [Test]
    public async Task StatPipelinedAsync_MatchesSequentialStatAsync()
    {
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);
        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var segmentIds = new SegmentId[]
        {
            ValidSegmentIds[0],
            "invalid@segment.id",
            ValidSegmentIds[2],
            ValidSegmentIds[3]
        };

        var pipelined = await client.StatPipelinedAsync(segmentIds, cancellationToken);
        var sequential = new List<UsenetStatResponse>();
        foreach (var segmentId in segmentIds)
        {
            sequential.Add(await client.StatAsync(segmentId, cancellationToken));
        }

        Assert.That(pipelined, Has.Count.EqualTo(sequential.Count));
        for (var index = 0; index < sequential.Count; index++)
        {
            Assert.That(pipelined[index].ArticleExists, Is.EqualTo(sequential[index].ArticleExists));
            Assert.That(pipelined[index].ResponseCode, Is.EqualTo(sequential[index].ResponseCode));
        }
    }

    [Test]
    public async Task StatPipelinedAsync_BatchLargerThanMaxPipelineDepth_Succeeds()
    {
        var client = new UsenetClient(new UsenetClientOptions
        {
            MaxPipelineDepth = 2
        });
        var cancellationToken = CancellationToken.None;

        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);
        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var segmentIds = ValidSegmentIds.Select(id => (SegmentId)id).ToArray();
        var results = await client.StatPipelinedAsync(segmentIds, cancellationToken);

        Assert.That(results, Has.Count.EqualTo(ValidSegmentIds.Length));
        Assert.That(results.Select(r => r.ArticleExists), Is.All.True);
    }
}
