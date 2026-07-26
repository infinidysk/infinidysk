using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class NzbResolutionCacheRetentionServiceTests
{
    [Fact]
    public async Task HostStartAsync_CompletesWhileHydrateIsStillBlocked()
    {
        var hydrateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHydrate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new BlockingHydrateCache(hydrateEntered, releaseHydrate);

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<NzbResolutionCache>(cache);
                services.AddSingleton(new ConfigManager());
                services.AddHostedService<NzbResolutionCacheRetentionService>();
            })
            .Build();

        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StartAsync(startCts.Token);

        // Host StartAsync must return before hydrate finishes — otherwise /health
        // stays down for the entrypoint's 30s window (#665).
        await hydrateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(releaseHydrate.Task.IsCompleted);

        await host.StopAsync(CancellationToken.None);
        releaseHydrate.TrySetResult();
    }

    [Fact]
    public async Task CancelledHydrate_DoesNotFaultTheHost()
    {
        var hydrateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHydrate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new BlockingHydrateCache(hydrateEntered, releaseHydrate);

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<NzbResolutionCache>(cache);
                services.AddSingleton(new ConfigManager());
                services.AddHostedService<NzbResolutionCacheRetentionService>();
            })
            .Build();

        await host.StartAsync(CancellationToken.None);
        await hydrateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Cancel while hydrate is blocked; service must catch and not stop the host
        // via BackgroundServiceExceptionBehavior.
        var stopTask = host.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        releaseHydrate.TrySetResult();
    }

    private sealed class BlockingHydrateCache(
        TaskCompletionSource hydrateEntered,
        TaskCompletionSource releaseHydrate)
        : NzbResolutionCache(() => throw new InvalidOperationException("test cache must not open SQLite"))
    {
        public override async Task HydrateAsync(TimeSpan ttl, CancellationToken ct)
        {
            hydrateEntered.TrySetResult();
            using var reg = ct.Register(() => releaseHydrate.TrySetCanceled(ct));
            await releaseHydrate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public override Task PurgeExpiredAsync(TimeSpan ttl, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
