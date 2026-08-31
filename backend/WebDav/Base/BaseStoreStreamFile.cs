using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.WebDav.Base;

public abstract class BaseStoreStreamFile(HttpContext context, ConfigManager configManager)
    : BaseStoreReadonlyItem, IDetachedStreamSource
{
    // Derived stream files must use these properties instead of capturing
    // the primary-constructor parameters (CS9107 double-capture).
    protected HttpContext Context => context;
    protected ConfigManager Config => configManager;

    protected abstract Task<Stream> GetStreamAsync(CancellationToken cancellationToken);

    public override async Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
    {
        var ownership = CreateStreamingScope(cancellationToken);
        context.Response.OnCompleted(async () =>
        {
            await ownership.DisposeAsync().ConfigureAwait(false);
        });

        try
        {
            return await GetStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ownership.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DetachedStreamLease> GetDetachedReadableStreamAsync(CancellationToken cancellationToken)
    {
        var ownership = CreateStreamingScope(cancellationToken);
        try
        {
            var stream = await GetStreamAsync(cancellationToken).ConfigureAwait(false);
            return new DetachedStreamLease
            {
                Stream = stream,
                Ownership = ownership,
                DavItem = Context.Items["DavItem"] as DavItem,
            };
        }
        catch
        {
            await ownership.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Request-token or entry-token contexts plus the per-stream semaphore.
    /// Each call allocates fresh instances so the response-registered path and
    /// the entry-owned path never share disposables.
    /// </summary>
    private StreamingScope CreateStreamingScope(CancellationToken token)
    {
        var streamSemaphore = CreatePerStreamSemaphore();
        var downloadPriorityContext = new DownloadPriorityContext()
        {
            Priority = SemaphorePriority.High,
            StreamSemaphore = streamSemaphore,
        };
#pragma warning disable CA2000 // ownership handle disposes the token-keyed context
        var scopedDownloadPriorityContext = token.SetContext(downloadPriorityContext);
#pragma warning restore CA2000

        var streamingTimeoutContext = new StreamingTimeoutContext
        {
            PerSegmentTimeout = configManager.GetStreamingSegmentTimeout(),
            MaxRetries = configManager.GetStreamingSegmentRetries(),
        };
#pragma warning disable CA2000 // ownership handle disposes the token-keyed context
        var scopedStreamingTimeoutContext = token.SetContext(streamingTimeoutContext);
#pragma warning restore CA2000

        IDisposable? scopedSchedulingContext = null;
        if (configManager.IsFiniteRangeSchedulerEnabled())
        {
            var capacityProvider = Context.RequestServices
                .GetRequiredService<StreamingCapacitySnapshotProvider>();
#pragma warning disable CA2000 // ownership handle is disposed by StreamingScope
            scopedSchedulingContext = token.SetContext(new StreamingSchedulingContext
            {
                Snapshot = capacityProvider.Capture(),
            });
#pragma warning restore CA2000
        }

        // Keep this stream's per-stream budget in sync with live config changes,
        // mirroring how DownloadingNntpClient resizes the shared streaming semaphore.
        // The per-stream count depends on the total connection setting, the preset,
        // and (in auto mode) the provider pool. The per-stream enable toggle is
        // intentionally excluded: the mode is decided once per stream at start.
        EventHandler<ConfigManager.ConfigEventArgs>? onConfigChanged = null;
        if (streamSemaphore is { } perStreamSemaphore)
        {
            onConfigChanged = (_, e) =>
            {
                if (e.ChangedConfig.ContainsKey(ConfigKeys.UsenetMaxDownloadConnections)
                    || e.ChangedConfig.ContainsKey(ConfigKeys.UsenetMaxDownloadConnectionsPerStreamPreset)
                    || e.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders))
                {
                    // The response may complete (and dispose the semaphore) concurrently
                    // with a config save; never let that surface into the save path.
                    try { perStreamSemaphore.UpdateMaxAllowed(configManager.GetMaxDownloadConnectionsPerStreamCount()); }
                    catch (ObjectDisposedException) { /* stream already ended */ }
                }
            };
            configManager.OnConfigChanged += onConfigChanged;
        }

        return new StreamingScope(
            configManager,
            onConfigChanged,
            scopedDownloadPriorityContext,
            scopedStreamingTimeoutContext,
            scopedSchedulingContext,
            streamSemaphore);
    }

    // In "per stream" mode each playback session gets its own streaming semaphore
    // so concurrent streams don't share a single global budget. Returns null when
    // the mode is disabled — the shared global semaphore in DownloadingNntpClient
    // is used instead. The provider connection pool still caps real connections.
    private PrioritizedSemaphore? CreatePerStreamSemaphore()
    {
        if (!configManager.IsMaxDownloadConnectionsPerStream()) return null;
        var max = configManager.GetMaxDownloadConnectionsPerStreamCount();
        return new PrioritizedSemaphore(max, max, configManager.GetStreamingPriority());
    }

    private sealed class StreamingScope(
        ConfigManager configManager,
        EventHandler<ConfigManager.ConfigEventArgs>? onConfigChanged,
        IDisposable downloadPriorityContext,
        IDisposable streamingTimeoutContext,
        IDisposable? schedulingContext,
        PrioritizedSemaphore? streamSemaphore) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;

            if (onConfigChanged is not null)
                configManager.OnConfigChanged -= onConfigChanged;
            downloadPriorityContext.Dispose();
            streamingTimeoutContext.Dispose();
            schedulingContext?.Dispose();
            streamSemaphore?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
