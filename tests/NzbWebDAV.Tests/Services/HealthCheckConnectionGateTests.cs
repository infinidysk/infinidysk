using System.Text.Json;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class HealthCheckConnectionGateTests
{
    [Fact]
    public async Task AcquireAsync_EnforcesAggregateLimit()
    {
        var config = CreateConfig(2);
        using var gate = new HealthCheckConnectionGate(config);
        using var first = await gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);
        using var second = await gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);

        var waiting = gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        first.Dispose();
        using var third = await waiting.WaitAsync(TimeSpan.FromSeconds(1));

        var snapshot = gate.GetSnapshot();
        Assert.Equal(2, snapshot.Active);
        Assert.Equal(0, snapshot.WaitingBackground);
    }

    [Fact]
    public async Task Release_AdmitsQueueBeforeBackground()
    {
        var config = CreateConfig(1);
        using var gate = new HealthCheckConnectionGate(config);
        using var active = await gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);
        var background = gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);
        var queue = gate.AcquireAsync(
            HealthCheckAdmissionPriority.Queue, CancellationToken.None);

        active.Dispose();
        using var queueLease = await queue.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(background.IsCompleted);

        queueLease.Dispose();
        using var backgroundLease = await background.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Decrease_DrainsWithoutCancellingActiveLeases()
    {
        var config = CreateConfig(3);
        using var gate = new HealthCheckConnectionGate(config);
        var leases = new[]
        {
            await gate.AcquireAsync(HealthCheckAdmissionPriority.Background, CancellationToken.None),
            await gate.AcquireAsync(HealthCheckAdmissionPriority.Background, CancellationToken.None),
            await gate.AcquireAsync(HealthCheckAdmissionPriority.Background, CancellationToken.None),
        };
        var waiting = gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);

        SetLimit(config, 1);
        leases[0].Dispose();
        leases[1].Dispose();
        Assert.False(waiting.IsCompleted);

        leases[2].Dispose();
        using var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, gate.GetSnapshot().Active);
    }

    [Fact]
    public async Task Increase_WakesWaitingWorkImmediately()
    {
        var config = CreateConfig(1);
        using var gate = new HealthCheckConnectionGate(config);
        using var active = await gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);
        var second = gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);
        var third = gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);

        SetLimit(config, 3);

        using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(1));
        using var thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(3, gate.GetSnapshot().Active);
    }

    [Fact]
    public async Task ProviderCapacityIncrease_WakesWaitingWorkImmediately()
    {
        var config = CreateConfig(limit: 2, providerCapacity: 1);
        using var gate = new HealthCheckConnectionGate(config);
        using var active = await gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);
        var waiting = gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        SetProviderCapacity(config, 2);

        using var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, gate.GetSnapshot().Active);
    }

    [Fact]
    public async Task AcquireAsync_CancelsWaitingAdmissionCleanly()
    {
        var config = CreateConfig(1);
        using var gate = new HealthCheckConnectionGate(config);
        using var active = await gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiting = gate.AcquireAsync(HealthCheckAdmissionPriority.Background, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, gate.GetSnapshot().WaitingBackground);
    }

    [Fact]
    public async Task AcquireAsync_WithAlreadyCancelledToken_DoesNotQueueWaiter()
    {
        var config = CreateConfig(1);
        using var gate = new HealthCheckConnectionGate(config);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.AcquireAsync(HealthCheckAdmissionPriority.Background, cts.Token));

        Assert.Equal(0, gate.GetSnapshot().Active);
        Assert.Equal(0, gate.GetSnapshot().WaitingBackground);
    }

    [Fact]
    public async Task Dispose_FaultsPendingWaiters()
    {
        var config = CreateConfig(1);
        var gate = new HealthCheckConnectionGate(config);
        using var active = await gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);
        var waiting = gate.AcquireAsync(
            HealthCheckAdmissionPriority.Background, CancellationToken.None);

        gate.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
    }

    [Fact]
    public async Task ParallelAcquireRelease_NeverExceedsConfiguredLimit()
    {
        const int limit = 4;
        var config = CreateConfig(limit);
        using var gate = new HealthCheckConnectionGate(config);
        var maximumObserved = 0;
        var tasks = Enumerable.Range(0, 64).Select(async _ =>
        {
            using var lease = await gate.AcquireAsync(
                HealthCheckAdmissionPriority.Background, CancellationToken.None);
            var active = gate.GetSnapshot().Active;
            var observed = Volatile.Read(ref maximumObserved);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(
                    ref maximumObserved,
                    active,
                    observed);
                if (previous == observed) break;
                observed = previous;
            }
            await Task.Yield();
        });

        await Task.WhenAll(tasks);

        Assert.InRange(maximumObserved, 1, limit);
        Assert.Equal(0, gate.GetSnapshot().Active);
    }

    private static ConfigManager CreateConfig(int limit, int providerCapacity = 200)
    {
        var config = new ConfigManager();
        SetProviderCapacity(config, providerCapacity);
        SetLimit(config, limit);
        return config;
    }

    private static void SetProviderCapacity(ConfigManager config, int providerCapacity)
    {
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
                {
                    Providers =
                    [
                        new UsenetProviderConfig.ConnectionDetails
                        {
                            ProviderId = Guid.NewGuid(),
                            Type = ProviderType.Pooled,
                            Host = "gate.example",
                            Port = 563,
                            UseSsl = true,
                            User = "user",
                            Pass = "pass",
                            MaxConnections = providerCapacity,
                        },
                    ],
                }),
            },
        ]);
    }

    private static void SetLimit(ConfigManager config, int limit)
    {
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKeys.RepairHealthcheckConcurrency,
                ConfigValue = limit.ToString(),
            },
        ]);
    }
}
