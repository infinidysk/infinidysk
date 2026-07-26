using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class NzbResolutionCacheRetentionServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // The #665 boot-loop guard: the entrypoint kills the backend when /health does not
    // answer within its retry window, so hydrating the play-token cache must never hold
    // up host startup.
    [Fact]
    public async Task HostStartAsync_ReturnsWhileHydrateBlocksItsThread()
    {
        var hydrateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseHydrate = new ManualResetEventSlim(false);
        using var host = BuildHost(new ThreadBlockingHydrateCache(hydrateEntered, releaseHydrate));

        var startTask = Task.Run(() => host.StartAsync(CancellationToken.None));
        try
        {
            await hydrateEntered.Task.WaitAsync(Timeout);
            var startCompleted = await Task.WhenAny(startTask, Task.Delay(Timeout)) == startTask;
            Assert.True(startCompleted, "Host startup must not wait for the play-token hydrate to finish.");
        }
        finally
        {
            // Always unblock the hydrate thread, otherwise a regression hangs the suite
            // instead of failing this test.
            releaseHydrate.Set();
        }

        await startTask.WaitAsync(Timeout);
        await host.StopAsync(CancellationToken.None).WaitAsync(Timeout);
    }

    [Fact]
    public async Task HydrateCancelledByShutdown_DoesNotFaultTheHostedService()
    {
        var hydrateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseHydrate = new ManualResetEventSlim(false);
        using var host = BuildHost(new ThreadBlockingHydrateCache(hydrateEntered, releaseHydrate));

        // Start off the test thread: a regression that hydrates inside StartAsync blocks
        // its caller outright, and this keeps that a timeout instead of a hung suite.
        var startTask = Task.Run(() => host.StartAsync(CancellationToken.None));
        try
        {
            await startTask.WaitAsync(Timeout);
            await hydrateEntered.Task.WaitAsync(Timeout);

            // Shutdown mid-hydrate must be swallowed, otherwise the faulted ExecuteAsync
            // stops the host through BackgroundServiceExceptionBehavior.
            await host.StopAsync(CancellationToken.None).WaitAsync(Timeout);

            var service = host.Services
                .GetServices<IHostedService>()
                .OfType<NzbResolutionCacheRetentionService>()
                .Single();
            Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        }
        finally
        {
            releaseHydrate.Set();
        }
    }

    private static IHost BuildHost(NzbResolutionCache cache) =>
        new HostBuilder()
            .ConfigureServices(services => services
                .AddSingleton(cache)
                .AddSingleton(new ConfigManager())
                .AddHostedService<NzbResolutionCacheRetentionService>())
            .Build();

    /// <summary>
    /// Blocks the calling thread inside HydrateAsync, mirroring a SQLite read that
    /// completes synchronously rather than yielding back to the host.
    /// </summary>
    private sealed class ThreadBlockingHydrateCache(
        TaskCompletionSource hydrateEntered,
        ManualResetEventSlim releaseHydrate)
        : NzbResolutionCache(() => throw new InvalidOperationException("the test cache must not open SQLite"))
    {
        public override Task HydrateAsync(TimeSpan ttl, CancellationToken ct)
        {
            hydrateEntered.TrySetResult();
            releaseHydrate.Wait(ct);
            return Task.CompletedTask;
        }
    }
}
