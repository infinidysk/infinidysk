using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// Where the daemon sits in the host's service list decides whether its mounts
/// are released before the container goes away.
/// </summary>
[Collection(nameof(HttpIntegrationCollection))]
public sealed class RcloneDaemonHostingTests(NzbDavWebApplicationFactory factory)
{
    [Fact]
    public void RcloneDaemon_StopsBeforeEveryOtherService()
    {
        // The host stops hosted services in reverse registration order, sharing
        // one HostOptions.ShutdownTimeout across all of them. Registered early,
        // the daemon stops last and releases its mounts on whatever is left of
        // that budget after every other service has stopped -- which can be
        // nothing. Registered last, it stops first and gets the whole window.
        var hosted = factory.Services.GetServices<IHostedService>().ToList();

        var daemon = hosted.FindIndex(service => service is RcloneDaemonService);
        var lastOwnService = hosted.FindLastIndex(
            service => service.GetType().FullName?.StartsWith("NzbWebDAV.", StringComparison.Ordinal) == true);

        Assert.True(daemon >= 0, "the built-in rclone daemon is not registered as a hosted service");
        Assert.Equal(lastOwnService, daemon);
    }
}
