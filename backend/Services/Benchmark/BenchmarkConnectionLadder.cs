using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services.Benchmark;

/// <summary>
/// A pool of ad-hoc NNTP connections held open for the duration of a benchmark
/// run. Reusing sockets across sweep levels avoids provider-side lingering-slot
/// refusals (no QUIT is sent on close) and keeps TCP windows warm, which is a
/// large part of making back-to-back runs repeatable.
/// </summary>
internal sealed class BenchmarkConnectionLadder(
    UsenetProviderConfig.ConnectionDetails provider,
    TimeSpan nntpReadTimeout) : IDisposable
{
    private readonly List<INntpClient> _connections = [];

    public int Count => _connections.Count;
    public IReadOnlyList<INntpClient> Connections => _connections;
    internal TimeSpan NntpReadTimeout { get; } = nntpReadTimeout;

    internal Func<UsenetProviderConfig.ConnectionDetails, TimeSpan, CancellationToken, ValueTask<INntpClient>>
        CreateConnection { get; init; } =
        static (details, timeout, ct) =>
            UsenetStreamingClient.CreateNewConnection(details, timeout, ct, applyBandwidthLimit: false);

    /// <summary>Grows the ladder to <paramref name="target"/> connections; returns the achieved count.</summary>
    public async Task<int> EnsureAsync(int target, CancellationToken ct)
    {
        while (_connections.Count < target)
        {
            ct.ThrowIfCancellationRequested();
            Exception? failure = null;
            INntpClient? conn = null;
            for (var attempt = 0; attempt < 2 && conn == null; attempt++)
            {
                try
                {
                    conn = await CreateConnection(provider, NntpReadTimeout, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e) when (!e.IsCancellationException(ct) && e is not OutOfMemoryException)
                {
                    failure = e;
                    // Benchmark retry pacing is independent of pool replacement spacing
                    // (usenet.reconnect-delay-milliseconds). The ladder probes the
                    // provider ceiling with ad-hoc sockets, not poisoned pool replacements.
                    if (attempt == 0)
                        await SafeDelay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                }
            }

            if (conn == null)
            {
                // First connection failing = config/credentials problem — surface it.
                if (_connections.Count == 0 && failure != null)
                    ExceptionDispatchInfo.Capture(failure).Throw();
                Log.Debug(failure, "Benchmark could not open an additional connection (treating as provider ceiling).");
                break;
            }

            _connections.Add(conn);
        }

        return _connections.Count;
    }

    /// <summary>Disposes and forgets connections that failed mid-measurement.</summary>
    public void Prune(IReadOnlyCollection<INntpClient> dead)
    {
        foreach (var conn in dead)
        {
            if (_connections.Remove(conn)) SafeDispose(conn);
        }
    }

    public void ShrinkTo(int target)
    {
        while (_connections.Count > Math.Max(0, target))
        {
            var conn = _connections[^1];
            _connections.RemoveAt(_connections.Count - 1);
            SafeDispose(conn);
        }
    }

    public void Dispose() => ShrinkTo(0);

    private static void SafeDispose(INntpClient conn)
    {
        try
        {
            conn.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Failed to dispose benchmark connection.");
        }
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation ends the delay early; callers re-check the token.
        }
    }
}
