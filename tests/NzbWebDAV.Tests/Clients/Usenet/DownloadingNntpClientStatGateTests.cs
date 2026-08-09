using System.Collections.Concurrent;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class DownloadingNntpClientStatGateTests
{
    [Fact]
    public async Task StatAsync_RespectsMaxQueueConnections()
    {
        var gate = new ManualResetEventSlim(false);
        var inFlight = 0;
        var maxInFlight = 0;
        var fake = new BlockingStatNntpClient(gate, () =>
        {
            var current = Interlocked.Increment(ref inFlight);
            Interlocked.Exchange(ref maxInFlight, Math.Max(Volatile.Read(ref maxInFlight), current));
        }, () => Interlocked.Decrement(ref inFlight));

        var config = CreateConfig(maxQueueConnections: 2, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(fake, config);

        var tasks = Enumerable.Range(0, 10)
            .Select(i => client.StatAsync(new SegmentId($"seg-{i}"), CancellationToken.None))
            .ToArray();

        await Task.Delay(100);
        Assert.True(Volatile.Read(ref maxInFlight) <= 2);

        gate.Set();
        await Task.WhenAll(tasks);

        Assert.True(maxInFlight <= 2);
        Assert.Equal(0, Volatile.Read(ref inFlight));
    }

    [Fact]
    public async Task QueueBudget_FollowsPresetChangesWithoutRestart()
    {
        var gate = new ManualResetEventSlim(false);
        var inFlight = 0;
        var fake = new BlockingStatNntpClient(gate,
            () => Interlocked.Increment(ref inFlight),
            () => Interlocked.Decrement(ref inFlight));

        // "low" is a quarter of the eight pooled connections.
        var config = CreatePresetConfig("low", poolConnections: 8);
        Assert.Equal(2, config.GetMaxQueueConnections());
        using var client = new DownloadingNntpClient(fake, config);

        var tasks = Enumerable.Range(0, 8)
            .Select(i => client.StatAsync(new SegmentId($"seg-{i}"), CancellationToken.None))
            .ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref inFlight) == 2, TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(2, Volatile.Read(ref inFlight));

        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxQueueConnectionsPreset, ConfigValue = "max" },
        ]);

        await WaitUntilAsync(() => Volatile.Read(ref inFlight) == 8, TimeSpan.FromSeconds(2));

        gate.Set();
        await Task.WhenAll(tasks);
        Assert.Equal(0, Volatile.Read(ref inFlight));
    }

    [Fact]
    public async Task StatAsync_CancellationWhileWaiting_DoesNotLeakPermit()
    {
        var holdFirst = new ManualResetEventSlim(false);
        var fake = new BlockingStatNntpClient(holdFirst, onEnter: null, onExit: null);
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(fake, config);

        var first = client.StatAsync(new SegmentId("held"), CancellationToken.None);
        await Task.Delay(50);

        using var cts = new CancellationTokenSource();
        var waiting = client.StatAsync(new SegmentId("waiting"), cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);

        holdFirst.Set();
        await first;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await client.StatAsync(new SegmentId("after"), timeout.Token);
    }

    [Fact]
    public async Task HeadAsync_RespectsMaxQueueConnections()
    {
        var gate = new ManualResetEventSlim(false);
        var inFlight = 0;
        var maxInFlight = 0;
        var fake = new BlockingHeadNntpClient(gate, () =>
        {
            var current = Interlocked.Increment(ref inFlight);
            Interlocked.Exchange(ref maxInFlight, Math.Max(Volatile.Read(ref maxInFlight), current));
        }, () => Interlocked.Decrement(ref inFlight));

        var config = CreateConfig(maxQueueConnections: 2, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(fake, config);

        var tasks = Enumerable.Range(0, 8)
            .Select(i => client.HeadAsync(new SegmentId($"seg-{i}"), CancellationToken.None))
            .ToArray();

        await Task.Delay(100);
        Assert.True(Volatile.Read(ref maxInFlight) <= 2);

        gate.Set();
        await Task.WhenAll(tasks);
        Assert.True(maxInFlight <= 2);
    }

    [Fact]
    public async Task StatAsync_PrimaryQueueContext_AdmittedBeforeSecondaryWaiters()
    {
        var holdSecondary = new ManualResetEventSlim(false);
        var holdPrimary = new ManualResetEventSlim(false);
        var entered = new ConcurrentQueue<string>();
        var fake = new SelectiveBlockingStatNntpClient(
            segmentId =>
            {
                entered.Enqueue(segmentId);
                return segmentId switch
                {
                    "secondary-held" => holdSecondary,
                    "primary-waiting" => holdPrimary,
                    _ => null,
                };
            });

        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10, poolConnections: 20);
        using var client = new DownloadingNntpClient(fake, config);

        var secondaryCtx = new QueueDownloadContext
        {
            IsPrimary = false,
            GetFanOutConcurrency = () => 1,
        };
        var primaryCtx = new QueueDownloadContext
        {
            IsPrimary = true,
            GetFanOutConcurrency = () => 1,
        };

        using var secondaryHeldCts = new CancellationTokenSource();
        using var secondaryHeldReg = secondaryHeldCts.Token.SetContext(secondaryCtx);
        var heldSecondary = client.StatAsync(new SegmentId("secondary-held"), secondaryHeldCts.Token);
        await WaitUntilAsync(() => entered.Contains("secondary-held"), TimeSpan.FromSeconds(2));

        using var primaryCts = new CancellationTokenSource();
        using var primaryReg = primaryCts.Token.SetContext(primaryCtx);
        var primary = client.StatAsync(new SegmentId("primary-waiting"), primaryCts.Token);

        using var secondaryWaitingCts = new CancellationTokenSource();
        using var secondaryWaitingReg = secondaryWaitingCts.Token.SetContext(secondaryCtx);
        var waitingSecondary = client.StatAsync(new SegmentId("secondary-waiting"), secondaryWaitingCts.Token);

        await Task.Delay(80);
        Assert.DoesNotContain("primary-waiting", entered);
        Assert.DoesNotContain("secondary-waiting", entered);

        // Free the held secondary; the waiting primary (High lane) should run next
        // and remain in-flight while we assert the Low-lane secondary is still waiting.
        holdSecondary.Set();
        await WaitUntilAsync(() => entered.Contains("primary-waiting"), TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.DoesNotContain("secondary-waiting", entered);

        holdPrimary.Set();
        await Task.WhenAll(primary, heldSecondary, waitingSecondary);

        var order = entered.ToArray();
        Assert.Equal(
            new[] { "secondary-held", "primary-waiting", "secondary-waiting" },
            order);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_RecordsSemaphoreWait_UnderQueueContention()
    {
        var holdFirst = new ManualResetEventSlim(false);
        var entered = new ConcurrentQueue<string>();
        var fake = new BlockingPipelinedBodyNntpClient(holdFirst, entered);
        var config = CreateConfig(maxQueueConnections: 1, maxDownloadConnections: 10);
        using var client = new DownloadingNntpClient(fake, config);

        var heldCtx = new QueueDownloadContext
        {
            IsPrimary = true,
            GetFanOutConcurrency = () => 1,
        };
        var waitingCtx = new QueueDownloadContext
        {
            IsPrimary = false,
            GetFanOutConcurrency = () => 1,
        };

        using var heldCts = new CancellationTokenSource();
        using var heldReg = heldCts.Token.SetContext(heldCtx);
        var heldTask = CollectPipelinedBodiesAsync(client, ["held"], heldCts.Token);
        await WaitUntilAsync(() => entered.Contains("held"), TimeSpan.FromSeconds(2));

        using var waitingCts = new CancellationTokenSource();
        using var waitingReg = waitingCts.Token.SetContext(waitingCtx);
        var waitingTask = CollectPipelinedBodiesAsync(client, ["waiting"], waitingCts.Token);

        await Task.Delay(80);
        Assert.DoesNotContain("waiting", entered);
        Assert.Equal(0, waitingCtx.SemaphoreWaitMilliseconds);

        holdFirst.Set();
        await Task.WhenAll(heldTask, waitingTask);

        Assert.Contains("waiting", entered);
        Assert.True(
            waitingCtx.SemaphoreWaitMilliseconds > 0,
            $"Expected pipelined wait to record semaphore contention, got {waitingCtx.SemaphoreWaitMilliseconds}ms");
    }

    private static async Task<List<PipelinedBodyResult>> CollectPipelinedBodiesAsync(
        DownloadingNntpClient client,
        IReadOnlyList<string> segmentIds,
        CancellationToken cancellationToken)
    {
        var results = new List<PipelinedBodyResult>();
        await foreach (var result in client.DecodedBodiesPipelinedAsync(
                           segmentIds, depth: 1, cancellationToken).ConfigureAwait(false))
        {
            results.Add(result);
        }

        return results;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"Condition not met within {timeout.TotalSeconds:0.#}s");
    }

    private static ConfigManager CreateConfig(
        int maxQueueConnections,
        int maxDownloadConnections,
        int poolConnections = 50)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue =
                    $$"""{"providers":[{"host":"nntp.example","port":563,"useSsl":true,"user":"u","pass":"p","maxConnections":{{poolConnections}},"type":1}]}""",
            },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxQueueConnections, ConfigValue = maxQueueConnections.ToString() },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxDownloadConnections, ConfigValue = maxDownloadConnections.ToString() },
        ]);
        return config;
    }

    // Provider JSON is serialized from the model rather than hand-written: config
    // deserialization is case-sensitive, so camelCased literals bind to nothing and
    // the pooled total silently collapses to 1.
    private static ConfigManager CreatePresetConfig(string preset, int poolConnections)
    {
        var providers = JsonSerializer.Serialize(new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = "nntp.example",
                    Port = 563,
                    UseSsl = true,
                    User = "u",
                    Pass = "p",
                    MaxConnections = poolConnections,
                },
            ]
        });

        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.UsenetProviders, ConfigValue = providers },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxQueueConnectionsPreset, ConfigValue = preset },
            new ConfigItem { ConfigName = ConfigKeys.UsenetMaxDownloadConnections, ConfigValue = "10" },
        ]);
        return config;
    }

    private abstract class MinimalNntpClient : NntpClient
    {
        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetStatResponse
            {
                ResponseCode = (int)UsenetResponseType.ArticleExists,
                ResponseMessage = $"223 0 0 <{segmentId}>",
                ArticleExists = true,
            });

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetHeadResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadFollows,
                ResponseMessage = "221",
                ArticleHeaders = null!,
            });

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    private sealed class SelectiveBlockingStatNntpClient(Func<string, ManualResetEventSlim?> gateFor)
        : MinimalNntpClient
    {
        public override async Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            var gate = gateFor(segmentId.ToString());
            if (gate is not null)
            {
                while (!gate.IsSet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }

            return await base.StatAsync(segmentId, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class RecordingStatNntpClient(INntpClient inner, ConcurrentQueue<string> entered) : MinimalNntpClient
    {
        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            entered.Enqueue(segmentId.ToString());
            return inner.StatAsync(segmentId, cancellationToken);
        }
    }

    private sealed class BlockingStatNntpClient(
        ManualResetEventSlim gate,
        Action? onEnter,
        Action? onExit) : MinimalNntpClient
    {
        public override async Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            onEnter?.Invoke();
            try
            {
                while (!gate.IsSet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }

                return await base.StatAsync(segmentId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                onExit?.Invoke();
            }
        }
    }

    private sealed class BlockingHeadNntpClient(
        ManualResetEventSlim gate,
        Action? onEnter,
        Action? onExit) : MinimalNntpClient
    {
        public override async Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            onEnter?.Invoke();
            try
            {
                while (!gate.IsSet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }

                return await base.HeadAsync(segmentId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                onExit?.Invoke();
            }
        }
    }

    private sealed class BlockingPipelinedBodyNntpClient(
        ManualResetEventSlim gate,
        ConcurrentQueue<string> entered) : MinimalNntpClient
    {
        public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var segmentId in segmentIds)
            {
                entered.Enqueue(segmentId);
                while (!gate.IsSet)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }

                yield return new PipelinedBodyResult
                {
                    SegmentId = segmentId,
                    Found = true,
                };
            }
        }
    }
}
