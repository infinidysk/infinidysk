using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Hosting;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class HostShutdownConventionTests(NzbDavWebApplicationFactory factory)
{
    [Fact]
    public void HostShutdownTimeout_IsFiveSeconds()
    {
        var options = factory.Services.GetRequiredService<IOptions<HostOptions>>();
        Assert.Equal(TimeSpan.FromSeconds(5), options.Value.ShutdownTimeout);
    }
}
