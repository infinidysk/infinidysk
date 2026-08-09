using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.WebDav.Base;

public abstract class BaseStoreStreamFile(HttpContext context, ConfigManager configManager) : BaseStoreReadonlyItem
{
    // Derived stream files must use these properties instead of capturing
    // the primary-constructor parameters (CS9107 double-capture).
    protected HttpContext Context => context;
    protected ConfigManager Config => configManager;

    protected abstract Task<Stream> GetStreamAsync(CancellationToken cancellationToken);

    public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
    {
        var streamSemaphore = CreatePerStreamSemaphore();
        var downloadPriorityContext = new DownloadPriorityContext()
        {
            Priority = SemaphorePriority.High,
            StreamSemaphore = streamSemaphore,
        };
        #pragma warning disable CA2000 // scoped context is disposed via Response.OnCompleted when the response completes
        var scopedDownloadPriorityContext = cancellationToken.SetContext(downloadPriorityContext);
        #pragma warning restore CA2000

        var streamingTimeoutContext = new StreamingTimeoutContext
        {
            PerSegmentTimeout = configManager.GetStreamingSegmentTimeout(),
            MaxRetries = configManager.GetStreamingSegmentRetries(),
        };
        #pragma warning disable CA2000 // scoped context is disposed via Response.OnCompleted when the response completes
        var scopedStreamingTimeoutContext = cancellationToken.SetContext(streamingTimeoutContext);
        #pragma warning restore CA2000

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

        context.Response.OnCompleted(() =>
        {
            if (onConfigChanged is not null) configManager.OnConfigChanged -= onConfigChanged;
            scopedDownloadPriorityContext.Dispose();
            scopedStreamingTimeoutContext.Dispose();
            streamSemaphore?.Dispose();
            return Task.CompletedTask;
        });

        return GetStreamAsync(cancellationToken);
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
}
