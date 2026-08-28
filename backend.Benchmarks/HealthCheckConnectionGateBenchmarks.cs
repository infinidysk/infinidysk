using System.Text.Json;
using BenchmarkDotNet.Attributes;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Benchmarks;

[MemoryDiagnoser]
public sealed class HealthCheckConnectionGateBenchmarks : IDisposable
{
    private const int OperationsPerRun = 256;
    private HealthCheckConnectionGate _gate = null!;

    [Params(1, 8, 32)]
    public int Contenders { get; set; }

    [Params(1, 8, 32)]
    public int Limit { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var config = new ConfigManager();
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
                            Host = "health-gate-benchmark.example",
                            Port = 563,
                            UseSsl = true,
                            User = "benchmark",
                            Pass = "benchmark",
                            MaxConnections = 64,
                        },
                    ],
                }),
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.RepairHealthcheckConcurrency,
                ConfigValue = Limit.ToString(),
            },
        ]);
        _gate = new HealthCheckConnectionGate(config);
    }

    [Benchmark(OperationsPerInvoke = OperationsPerRun)]
    public void ContendedAcquireRelease()
    {
        Parallel.For(
            fromInclusive: 0,
            toExclusive: OperationsPerRun,
            new ParallelOptions { MaxDegreeOfParallelism = Contenders },
            index =>
            {
                using var lease = _gate.AcquireAsync(
                        index % 4 == 0
                            ? HealthCheckAdmissionPriority.Queue
                            : HealthCheckAdmissionPriority.Background,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                Thread.SpinWait(64);
            });
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _gate?.Dispose();
        _gate = null!;
    }
}
