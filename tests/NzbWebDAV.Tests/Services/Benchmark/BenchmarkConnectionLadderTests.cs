using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Benchmark;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Services.Benchmark;

public class BenchmarkConnectionLadderTests
{
    [Fact]
    public async Task EnsureAsync_PassesConfiguredNntpReadTimeoutToCreateConnection()
    {
        var timeout = TimeSpan.FromSeconds(17);
        TimeSpan? seenTimeout = null;
        var fake = new FakeNntpClient(new Dictionary<string, byte[]>());
        using var ladder = new BenchmarkConnectionLadder(MakeDetails(), timeout)
        {
            CreateConnection = (_, nntpReadTimeout, _) =>
            {
                seenTimeout = nntpReadTimeout;
                return ValueTask.FromResult<INntpClient>(fake);
            },
        };

        var opened = await ladder.EnsureAsync(1, CancellationToken.None);

        Assert.Equal(1, opened);
        Assert.Equal(timeout, ladder.NntpReadTimeout);
        Assert.Equal(timeout, seenTimeout);
    }

    private static UsenetProviderConfig.ConnectionDetails MakeDetails() =>
        new()
        {
            Type = ProviderType.Pooled,
            Host = "nntp.example",
            Port = 563,
            UseSsl = true,
            User = "u",
            Pass = "p",
            MaxConnections = 1,
        };
}
