using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models.Nzb;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class WrappingNntpClient(INntpClient usenetClient) : NntpClient, INntpConnectionStats
{
    private const int MaxRetiringClients = 4;
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(250);

    private INntpClient _usenetClient = usenetClient;
    protected INntpClient InnerClient => _usenetClient;

    internal static INntpClient Unwrap(INntpClient client)
    {
        while (client is WrappingNntpClient wrap)
            client = wrap.InnerClient;
        return client;
    }
    private readonly ConcurrentDictionary<INntpClient, byte> _retiringClients = new();
    // Weak entries preserve retirement order for the cap without retaining every
    // successfully drained client for the lifetime of the wrapper.
    private readonly ConcurrentQueue<WeakReference<INntpClient>> _retirementOrder = new();
    private readonly Lock _retirementLock = new();

    public int InFlightConnections =>
        _usenetClient is INntpConnectionStats stats ? stats.InFlightConnections : 0;

    public override Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken) =>
        _usenetClient.ConnectAsync(host, port, useSsl, cancellationToken);

    public override Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken) =>
        _usenetClient.AuthenticateAsync(user, pass, cancellationToken);

    public override Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.StatAsync(segmentId, cancellationToken);

    public override Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.HeadAsync(segmentId, cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, cancellationToken);

    public override Task<UsenetDateResponse> DateAsync(
        CancellationToken cancellationToken) =>
        _usenetClient.DateAsync(cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodiesAsync(segmentIds, onConnectionReadyAgain, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);

    public override int PipeliningDepth => _usenetClient.PipeliningDepth;

    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string segmentId, CancellationToken cancellationToken) =>
        _usenetClient.AcquireExclusiveConnectionAsync(segmentId, cancellationToken);

    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken) =>
        _usenetClient.AcquireExclusiveConnectionAsync(segmentIds, cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, exclusiveConnection, cancellationToken);

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodiesAsync(segmentIds, exclusiveConnection, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, exclusiveConnection, cancellationToken);

    public override Task<UsenetYencHeader> GetYencHeadersAsync(
        string segmentId, CancellationToken cancellationToken) =>
        _usenetClient.GetYencHeadersAsync(segmentId, cancellationToken);

    public override Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken) =>
        _usenetClient.GetFileSizeAsync(file, cancellationToken);

    public override IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken) =>
        _usenetClient.StatsPipelinedAsync(segmentIds, depth, cancellationToken);

    public override IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodiesPipelinedAsync(segmentIds, depth, cancellationToken);

    public override IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticlesPipelinedAsync(segmentIds, depth, cancellationToken);

    /// <summary>
    /// Swap the live client immediately so new requests use new pools, then dispose the
    /// old client after all in-flight borrows drain.
    /// </summary>
    protected void ReplaceUnderlyingClient(INntpClient usenetClient)
    {
        var old = Interlocked.Exchange(ref _usenetClient, usenetClient);
        if (old is NntpClient oldNntpClient)
            oldNntpClient.Retire();
        EnqueueForRetirement(old);
    }

    /// <summary>
    /// Test hook: swap and wait for the old client to drain.
    /// Drains inline (no background loop) so exactly one consumer works the queue.
    /// </summary>
    internal Task ReplaceUnderlyingClientForTestsAsync(
        INntpClient usenetClient, CancellationToken cancellationToken = default)
    {
        var old = Interlocked.Exchange(ref _usenetClient, usenetClient);
        if (old is NntpClient oldNntpClient)
            oldNntpClient.Retire();
        EnqueueForRetirement(old, startDrain: false);
        return DrainRetiringClientAsync(old, cancellationToken);
    }

    private void EnqueueForRetirement(INntpClient old, bool startDrain = true)
    {
        lock (_retirementLock)
        {
            if (!_retiringClients.TryAdd(old, 0))
                return;

            _retirementOrder.Enqueue(new WeakReference<INntpClient>(old));
            TrimExcessRetiringClients();
        }

        if (startDrain)
            _ = Task.Run(() => DrainRetiringClientAsync(old, CancellationToken.None));
    }

    private void TrimExcessRetiringClients()
    {
        while (_retirementOrder.TryPeek(out var oldestReference))
        {
            // Completed drains remain in the ordering queue. Remove stale entries eagerly
            // so routine sequential saves do not accumulate ordering metadata.
            if (!oldestReference.TryGetTarget(out var oldest)
                || !_retiringClients.ContainsKey(oldest))
            {
                _retirementOrder.TryDequeue(out _);
                continue;
            }

            if (_retiringClients.Count <= MaxRetiringClients)
                break;

            _retirementOrder.TryDequeue(out _);
            if (!_retiringClients.TryRemove(oldest, out _))
                continue;

            Log.Warning(
                "Force-disposing the oldest retired NNTP client because more than " +
                "{MaxRetiringClients} client generations are still draining.",
                MaxRetiringClients);
            TryDispose(oldest);
        }
    }

    private async Task DrainRetiringClientAsync(
        INntpClient client,
        CancellationToken cancellationToken)
    {
        while (_retiringClients.ContainsKey(client))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inFlight = client is INntpConnectionStats stats
                ? stats.InFlightConnections
                : 0;

            if (inFlight == 0)
                break;

            await Task.Delay(DrainPollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (_retiringClients.TryRemove(client, out _))
            TryDispose(client);
    }

    private static void TryDispose(INntpClient client)
    {
        try
        {
            client.Dispose();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to dispose replaced NNTP client");
        }
    }

    public override void Dispose()
    {
        // Dispose the live client and anything still retiring.
        foreach (var retiring in _retiringClients.Keys)
        {
            if (_retiringClients.TryRemove(retiring, out _))
                TryDispose(retiring);
        }

        _usenetClient.Dispose();
        GC.SuppressFinalize(this);
    }

    internal override void Retire()
    {
        if (_usenetClient is NntpClient nntpClient)
            nntpClient.Retire();
    }
}
