using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
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
}
