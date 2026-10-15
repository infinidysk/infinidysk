using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Config;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class DiTopologyTests
{
    [Fact]
    public async Task Container_ResolvesHighestCouplingSeams()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        var services = factory.Services;

        var config = services.GetRequiredService<ConfigManager>();
        Assert.Same(config, services.GetRequiredService<IConfigReader>());
        Assert.Same(config, services.GetRequiredService<IConfigUpdater>());
        Assert.Same(config, services.GetRequiredService<IConfigChangeSource>());

        var blobStore = services.GetRequiredService<IBlobStore>();
        Assert.Same(blobStore, BlobStore.Current);

        Assert.Same(services.GetRequiredService<IRcloneClient>(), RcloneClient.Current);
        Assert.Same(
            services.GetRequiredService<WebsocketManager>(),
            services.GetRequiredService<IWebsocketPublisher>());
        Assert.Same(
            services.GetRequiredService<QueueManager>(),
            services.GetRequiredService<IQueueCoordinator>());
        Assert.Same(
            services.GetRequiredService<HealthCheckService>(),
            services.GetRequiredService<IHealthCheckQuiescence>());
    }

    [Fact]
    public async Task Container_InitializesPlaybackRepairTriggerBeforeFirstRequest()
    {
        var previousSink = Par2RepairTriggerSink.Current;
        try
        {
            Par2RepairTriggerSink.Current = null;
            await using var factory = new NzbDavWebApplicationFactory();

            var services = factory.Services;
            var initializedSink = Par2RepairTriggerSink.Current;

            Assert.NotNull(initializedSink);
            Assert.Same(
                initializedSink,
                services.GetRequiredService<Par2RepairTriggerSink>());
        }
        finally
        {
            Par2RepairTriggerSink.Current = previousSink;
        }
    }
}
