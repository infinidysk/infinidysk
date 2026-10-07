using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalStreamTraceCollection))]
public class StreamTraceConnectionWaitTests
{
    [Fact]
    public async Task ProviderPoolTimeoutThenSuccess_ReportsFailedWaitSeparately()
    {
        await WithTraceBufferAsync(async (buffer, session, range) =>
        {
            var inner = new FakeNntpClient(new Dictionary<string, byte[]> { ["seg"] = [1, 2, 3] });
            using var pool = new ConnectionPool<INntpClient>(
                maxConnections: 1,
                _ => ValueTask.FromResult<INntpClient>(inner));
            using var client = new MultiConnectionNntpClient(
                pool,
                ProviderType.Pooled,
                new ProviderCircuitBreaker("trace-timeout"),
                "trace-timeout",
                maxTransferConnections: 1);

            var blocker = await pool.GetConnectionLockAsync(SemaphorePriority.High);
            using (MultiProviderNntpClient.BeginStreamTraceRangeScope(range))
            {
                using var callerCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using (callerCts.Token.SetContext(new TransferAdmissionFailoverContext(
                           () => true, TimeSpan.FromMilliseconds(100))))
                {
                    await Assert.ThrowsAsync<ProviderTransferAdmissionTimeoutException>(() =>
                        client.DecodedBodyAsync("seg", callerCts.Token));
                }

                blocker.Dispose();
                var response = await client.DecodedBodyAsync("seg", callerCts.Token);
                if (response.Stream is not null)
                    await response.Stream.DisposeAsync();
            }

            var ended = EndRange(buffer, session, range);
            Assert.Equal(1, ended.FailedConnectionAttempts);
            Assert.True(ended.MaxFailedConnectionWaitMs >= 90, $"failed wait {ended.MaxFailedConnectionWaitMs}");
            Assert.Equal(ended.MaxFailedConnectionWaitMs, ended.FailedConnectionWaitMs);
            Assert.NotNull(ended.FirstConnectionWaitMs);
            Assert.True(ended.FirstConnectionWaitMs < ended.MaxFailedConnectionWaitMs);
            Assert.True((ended.MaxConnectionWaitMs ?? 0) < ended.MaxFailedConnectionWaitMs);
            Assert.Null(ended.PermitWaitMs);
        });
    }

    [Fact]
    public async Task OuterPermitContention_IsReportedAsPermitWaitNotConnectionWait()
    {
        await WithTraceBufferAsync(async (buffer, session, range) =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = ConfigKeys.UsenetProviders,
                    ConfigValue =
                        """{"providers":[{"host":"nntp.example","port":563,"useSsl":true,"user":"u","pass":"p","maxConnections":10,"type":1}]}""",
                },
                new ConfigItem { ConfigName = ConfigKeys.UsenetMaxQueueConnections, ConfigValue = "1" },
                new ConfigItem { ConfigName = ConfigKeys.UsenetMaxDownloadConnections, ConfigValue = "10" },
            ]);
            using var client = new DownloadingNntpClient(
                new GatedStatClient(new FakeNntpClient(new Dictionary<string, byte[]>()), gate.Task),
                config);

            var held = client.StatAsync(new SegmentId("held"), CancellationToken.None);
            Task waiting;
            using (MultiProviderNntpClient.BeginStreamTraceRangeScope(range))
                waiting = client.StatAsync(new SegmentId("waiting"), CancellationToken.None);

            await Task.Delay(150);
            gate.SetResult();
            await Task.WhenAll(held, waiting).WaitAsync(TimeSpan.FromSeconds(5));

            var ended = EndRange(buffer, session, range);
            Assert.True(ended.MaxPermitWaitMs >= 100, $"permit wait {ended.MaxPermitWaitMs}");
            Assert.Equal(ended.MaxPermitWaitMs, ended.PermitWaitMs);
            Assert.Null(ended.ConnectionWaitMs);
            Assert.Null(ended.FailedConnectionAttempts);
        });
    }

    private static async Task WithTraceBufferAsync(
        Func<StreamTraceBuffer, Guid, StreamTraceRangeContext, Task> body)
    {
        var previous = StreamTrace.Buffer;
        var buffer = new StreamTraceBuffer(capacity: 100, maxSessions: 10);
        StreamTrace.Configure(buffer);
        try
        {
            var session = Guid.NewGuid();
            var range = buffer.RangeOpen(session, "/view/a.bin", "GET", 0, null, 1000, null, null);
            Assert.NotNull(range);
            await body(buffer, session, range.Value);
        }
        finally
        {
            StreamTrace.Configure(previous ?? new StreamTraceBuffer(capacity: 1, maxSessions: 10, enabled: false));
        }
    }

    private static StreamTraceEvent EndRange(StreamTraceBuffer buffer, Guid session, StreamTraceRangeContext range)
    {
        buffer.RangeEnd(session, range, ReadSession.EndReasonCode.Completed, 0);
        return buffer.GetSessionEvents(session).Last();
    }

    private sealed class GatedStatClient(INntpClient inner, Task gate) : WrappingNntpClient(inner)
    {
        public override async Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.StatAsync(segmentId, cancellationToken).ConfigureAwait(false);
        }
    }
}
