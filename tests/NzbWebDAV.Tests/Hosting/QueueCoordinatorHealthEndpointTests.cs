using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Hosting;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class QueueCoordinatorHealthEndpointTests(NzbDavWebApplicationFactory factory)
{
    [Fact]
    public void LivenessAliases_ResolveToTheSameHostedServiceInstance()
    {
        var hosted = factory.Services.GetRequiredService<QueueCoordinatorHostedService>();
        var liveness = factory.Services.GetRequiredService<IQueueCoordinatorLiveness>();
        var hostedServices = factory.Services.GetRequiredService<IEnumerable<IHostedService>>();

        Assert.Same(hosted, liveness);
        Assert.Contains(hostedServices, service => ReferenceEquals(service, hosted));
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsOkOnceCoordinatorIsRunning()
    {
        var hosted = factory.Services.GetRequiredService<QueueCoordinatorHostedService>();
        await WaitUntilStateAsync(hosted, QueueCoordinatorState.Running);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthEndpoint_Returns503WhenCoordinatorIsFaulted_WhileReadyStaysHealthy()
    {
        await using var isolated = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQueueCoordinatorLiveness>();
                services.AddSingleton<IQueueCoordinatorLiveness>(new FaultedLiveness());
            });
        });

        using var client = isolated.CreateClient();

        using var health = await client.GetAsync("/health");
        using var ready = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal("Unhealthy", await health.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    private static async Task WaitUntilStateAsync(
        QueueCoordinatorHostedService service,
        QueueCoordinatorState expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (service.GetState() != expected)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Queue coordinator state was {service.GetState()}, expected {expected}.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FaultedLiveness : IQueueCoordinatorLiveness
    {
        public QueueCoordinatorState GetState() => QueueCoordinatorState.Faulted;
    }
}
