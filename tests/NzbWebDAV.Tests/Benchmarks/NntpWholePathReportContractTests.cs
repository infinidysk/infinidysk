using System.Net.Sockets;
using System.Text.Json;
using NzbWebDAV.Benchmarks;
using UsenetSharp.Clients;

namespace NzbWebDAV.Tests.Benchmarks;

public sealed class NntpWholePathReportContractTests
{
    [Fact]
    public void Corpus_IsDeterministicAndHasExpectedFileHash()
    {
        var first = NntpLoopbackCorpus.Create(articleCount: 4, decodedArticleBytes: 4096, seed: 123);
        var second = NntpLoopbackCorpus.Create(articleCount: 4, decodedArticleBytes: 4096, seed: 123);

        Assert.Equal(first.ExpectedSha256, second.ExpectedSha256);
        Assert.Equal(first.DecodedFile, second.DecodedFile);
        Assert.Equal(first.Articles.Select(article => article.WireBody), second.Articles.Select(article => article.WireBody));
        Assert.All(first.Articles, article =>
        {
            Assert.Contains("=ybegin", System.Text.Encoding.ASCII.GetString(article.WireBody));
            Assert.Contains("=yend", System.Text.Encoding.ASCII.GetString(article.WireBody));
            Assert.True(first.TryGetArticle($"<{article.SegmentId}>", out var found));
            Assert.Equal(article, found);
        });
    }

    [Fact]
    public async Task Server_WritesGreetingAndSnapshotThenDisposesConnections()
    {
        var corpus = NntpLoopbackCorpus.Create(articleCount: 1, decodedArticleBytes: 1024, seed: 123);
        var path = Path.Join(Path.GetTempPath(), $"nntp-loopback-{Guid.NewGuid():N}.json");
        try
        {
            await using var server = await NntpLoopbackServer.StartAsync(corpus);
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            Assert.StartsWith("200 ", await reader.ReadLineAsync());
            await server.WriteSnapshotAsync(path, CancellationToken.None);
            var snapshot = JsonSerializer.Deserialize<NntpLoopbackServerSnapshot>(await File.ReadAllTextAsync(path));
            Assert.NotNull(snapshot);
            Assert.Equal(0, snapshot.BodyCommands);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PerformanceReportJson_UsesStableWholePathFields()
    {
        var path = Path.Join(Path.GetTempPath(), $"nntp-whole-path-{Guid.NewGuid():N}.json");
        try
        {
            PerformanceReportJson.Write(
                path,
                NntpWholePathReport.ReportName,
                new Dictionary<string, ScenarioSnapshot>
                {
                    ["plain-buffered-w1"] = new(
                        new Dictionary<string, long>
                        {
                            ["expectedBytes"] = 1,
                            ["sha256Match"] = 1,
                        },
                        PerformanceReportJson.WholePathTiming(1, 2, 3, 4, 5, 6, 7, 8, 9, 10)),
                });

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.Equal(PerformanceReportJson.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(NntpWholePathReport.ReportName, root.GetProperty("report").GetString());
            var scenario = root.GetProperty("scenarios").GetProperty("plain-buffered-w1");
            Assert.Equal(1, scenario.GetProperty("deterministic").GetProperty("sha256Match").GetInt64());
            Assert.True(scenario.GetProperty("timing").TryGetProperty("throughputMbps", out _));
            Assert.Equal(6d, scenario.GetProperty("timing").GetProperty("timeToPeakActiveMs").GetDouble());
            Assert.Equal(7d, scenario.GetProperty("timing").GetProperty("clientAllocatedBytes").GetDouble());
            Assert.Equal(8d, scenario.GetProperty("timing").GetProperty("gen0Collections").GetDouble());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Server_RecordsHandshakeDelayAndTimeToPeakConnections()
    {
        var corpus = NntpLoopbackCorpus.Create(articleCount: 1, decodedArticleBytes: 1024, seed: 123);
        await using var server = await NntpLoopbackServer.StartAsync(corpus, handshakeDelayMs: 25);
        using var first = new TcpClient();
        using var second = new TcpClient();

        var greetingWait = System.Diagnostics.Stopwatch.StartNew();
        await first.ConnectAsync("127.0.0.1", server.Port);
        await second.ConnectAsync("127.0.0.1", server.Port);
        using var firstReader = new StreamReader(first.GetStream());
        using var secondReader = new StreamReader(second.GetStream());
        Assert.StartsWith("200 ", await firstReader.ReadLineAsync());
        Assert.True(greetingWait.Elapsed >= TimeSpan.FromMilliseconds(20));
        Assert.StartsWith("200 ", await secondReader.ReadLineAsync());

        var snapshot = server.GetSnapshot();
        Assert.Equal(2, snapshot.PeakActiveConnections);
        Assert.True(snapshot.TimeToPeakActiveMs >= 0);
    }

    [Theory]
    [InlineData("quick", 5)]
    [InlineData("sustained", 4)]
    [InlineData("profile", 1)]
    [InlineData("cold", 2)]
    public void ScenarioSets_AreNamedAndExplicitlyPlaintext(string set, int expectedCount)
    {
        var scenarios = NntpWholePathScenario.ForSet(set);

        Assert.Equal(expectedCount, scenarios.Count);
        Assert.All(scenarios, scenario =>
        {
            Assert.False(scenario.UseTls);
            Assert.Equal(YencCrcValidationMode.Require, scenario.CrcValidation);
        });
    }

    [Fact]
    public void ColdScenario_ModelsProductionSizedReadAndConnectionRamp()
    {
        var scenario = NntpWholePathScenario.Cold[0];

        Assert.Equal("cold-ramp-256mib-w4", scenario.Name);
        Assert.Equal(NntpWholePathLayer.HttpLike, scenario.Layer);
        Assert.Equal(342L * 768 * 1024, (long)scenario.ArticleCount * scenario.DecodedArticleBytes);
        Assert.Equal(20, scenario.ConnectionCount);
        Assert.Equal(4, scenario.BatchWidth);
        Assert.Equal(150, scenario.HandshakeDelayMs);
        Assert.Equal(40, scenario.ArticleBufferSize);
        Assert.False(scenario.PrewarmConnections);

        var prewarm = NntpWholePathScenario.Cold[1];
        Assert.Equal("cold-ramp-256mib-w4-prewarm", prewarm.Name);
        Assert.True(prewarm.PrewarmConnections);
    }

    [Fact]
    public async Task Cli_RejectsIncompleteLoopbackServerArguments()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PerformanceReportCli.TryHandleAsync(
                ["--nntp-loopback-server", "--articles", "1"]));
    }

    [Fact]
    public async Task Cli_RejectsNegativeHandshakeDelay()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PerformanceReportCli.TryHandleAsync(
            [
                "--nntp-loopback-server",
                "--articles", "1",
                "--article-bytes", "1024",
                "--handshake-ms", "-1",
                "--counters-out", "unused.json",
            ]));
    }

    [Fact]
    public async Task Cli_DoesNotIgnoreScenarioOptionsBeforeAnUnknownArgument()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PerformanceReportCli.TryHandleAsync(["--set", "sustained", "--unexpected"]));
    }

    [Fact]
    public async Task Cli_RejectsCopyChunkRepetitionsForOtherReports()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PerformanceReportCli.TryHandleAsync(
                ["--nntp-whole-path-report", "--repetitions", "3"]));
    }
}
