using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Websocket;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Services;

public class PlaybackFastVerifierBodyReleaseTests
{
    [Fact]
    public async Task VerifyAsync_BodyMode_ReleasesTheProbedBody()
    {
        var stream = new TrackingYencStream();
        var verifier = new PlaybackFastVerifier(BuildClient(stream));

        var outcome = await verifier.VerifyAsync(
            Nzb(), "body", sampleCount: 1, CancellationToken.None);

        Assert.Equal(PlaybackFastVerifier.Verdict.Available, outcome.Verdict);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task VerifyAsync_BodyMode_FailedReleaseKeepsTheVerdict()
    {
        var stream = new ThrowOnDisposeYencStream();
        var verifier = new PlaybackFastVerifier(BuildClient(stream));

        var outcome = await verifier.VerifyAsync(
            Nzb(), "body", sampleCount: 1, CancellationToken.None);

        // The response code already said the article is there. Failing to hand the body back
        // must not turn that into a failure.
        Assert.Equal(PlaybackFastVerifier.Verdict.Available, outcome.Verdict);
    }

    private static ScriptedBodyClient BuildClient(YencStream stream)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
        ]);

        return new ScriptedBodyClient(
            stream,
            configManager,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
    }

    private static MemoryStream Nzb()
    {
        var nzb = """
            <?xml version="1.0" encoding="utf-8"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject="test">
                <groups><group>alt.binaries.test</group></groups>
                <segments>
                  <segment bytes="100" number="1">seg@example.com</segment>
                </segments>
              </file>
            </nzb>
            """;
        return new MemoryStream(Encoding.UTF8.GetBytes(nzb));
    }

    private sealed class ScriptedBodyClient(
        YencStream stream,
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker,
        StreamTraceBuffer streamTrace,
        ActiveReadRegistry activeReadRegistry) : UsenetStreamingClient(
        configManager, websocketManager, usageTracker, metricsWriter, bytesTracker,
        streamTrace, activeReadRegistry)
    {
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = $"222 <{segmentId}>",
                Stream = stream,
            });
    }

    private sealed class TrackingYencStream : YencStream
    {
        public TrackingYencStream() : base(new MemoryStream([], writable: false))
        {
        }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowOnDisposeYencStream : YencStream
    {
        public ThrowOnDisposeYencStream() : base(new MemoryStream([], writable: false))
        {
        }

        protected override void Dispose(bool disposing) =>
            throw new IOException("connection reset while releasing the body");
    }
}
